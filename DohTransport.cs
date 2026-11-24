using System.Buffers.Binary;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;

namespace DnsPanel;

internal static class DohTransport
{
    public static HttpClient Open(IPAddress remote, IPAddress? local) =>
        new(new SocketsHttpHandler
        {
            UseProxy = false,
            UseCookies = false,
            AllowAutoRedirect = false,
            ConnectCallback = async (context, token) =>
            {
                var socket = new Socket(remote.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    if (local is not null)
                        socket.Bind(new IPEndPoint(local, 0));
                    await socket.ConnectAsync(remote, context.DnsEndPoint.Port, token).ConfigureAwait(false);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            }
        })
        { MaxResponseContentBufferSize = 65_535 };

    public static async Task<IPAddress[]> ResolveAsync(HttpClient client, Uri endpoint, string host, bool ipv6,
        CancellationToken token)
    {
        var type = (ushort)(ipv6 ? 28 : 1);
        var query = MakeQuery(host, type);
        var encoded = Convert.ToBase64String(query).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{endpoint}?dns={encoded}");
        request.Headers.Accept.ParseAdd("application/dns-message");
        using var response = await client.SendAsync(request, token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode || response.Content.Headers.ContentType?.MediaType != "application/dns-message")
            return [];
        var data = await response.Content.ReadAsByteArrayAsync(token).ConfigureAwait(false);
        return ReadAddresses(data, query, type);
    }

    private static byte[] MakeQuery(string host, ushort type)
    {
        using var message = new MemoryStream();
        message.Write([0, 0, 1, 0, 0, 1, 0, 0, 0, 0, 0, 0]);
        foreach (var label in host.Split('.'))
        {
            var bytes = Encoding.ASCII.GetBytes(label);
            message.WriteByte((byte)bytes.Length);
            message.Write(bytes);
        }
        message.Write([0, (byte)(type >> 8), (byte)type, 0, 1]);
        return message.ToArray();
    }

    private static IPAddress[] ReadAddresses(ReadOnlySpan<byte> data, byte[] query, ushort type)
    {
        if (data.Length < query.Length || data[0] != 0 || data[1] != 0 ||
            (BinaryPrimitives.ReadUInt16BigEndian(data[2..]) & 0xfa0f) != 0x8000 ||
            BinaryPrimitives.ReadUInt16BigEndian(data[4..]) != 1 ||
            !data[12..query.Length].SequenceEqual(query.AsSpan(12)))
            return [];

        var count = BinaryPrimitives.ReadUInt16BigEndian(data[6..]);
        var offset = query.Length;
        var addresses = new List<IPAddress>();
        for (var record = 0; record < count; record++)
        {
            offset = EndOfName(data, offset);
            if (offset < 0 || offset + 10 > data.Length)
                return [];
            var field = data[offset..];
            var length = BinaryPrimitives.ReadUInt16BigEndian(field[8..]);
            if (field.Length < 10 + length)
                return [];
            if (BinaryPrimitives.ReadUInt16BigEndian(field) == type &&
                BinaryPrimitives.ReadUInt16BigEndian(field[2..]) == 1 &&
                length == (type == 28 ? 16 : 4))
                addresses.Add(new IPAddress(field.Slice(10, length)));
            offset += 10 + length;
        }
        return addresses.Distinct().ToArray();
    }

    private static int EndOfName(ReadOnlySpan<byte> data, int offset)
    {
        while (offset < data.Length)
        {
            var length = data[offset++];
            if (length == 0)
                return offset;
            if ((length & 0xc0) == 0xc0)
                return offset < data.Length && ((length & 0x3f) << 8 | data[offset]) < offset - 1 ? offset + 1 : -1;
            if (length > 63)
                return -1;
            offset += length;
        }
        return -1;
    }
}
