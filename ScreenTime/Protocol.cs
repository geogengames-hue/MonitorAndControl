using System.Buffers.Binary;
using System.Text.Json;

namespace MonitorAndControl.ScreenTime;

public sealed class ScreenTimeRequest
{
    public string Command { get; set; } = "snapshot";
    public string UserSid { get; set; } = "";
    public int DailyMinutes { get; set; }
    public int BonusMinutes { get; set; }
    public bool Enabled { get; set; }
    public string TicketId { get; set; } = "";
}
public sealed record ScreenTimeReply(bool Ok, string? Error, JsonElement? Data)
{
    public static ScreenTimeReply Success(object value) =>
        new(true, null, JsonSerializer.SerializeToElement(value, ScreenTimeProtocol.Json));
    public static ScreenTimeReply Failure(string message) => new(false, message, null);
}
public static class ScreenTimeProtocol
{
    public const string PipeName = "DeviceMon.ScreenTime.v1";
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static async Task WriteAsync<T>(Stream stream, T value, CancellationToken token)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        try
        {
            await stream.WriteAsync(header, token);
            await stream.WriteAsync(payload, token);
            await stream.FlushAsync(token);
        }
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(payload); }
    }
    public static async Task<T> ReadAsync<T>(Stream stream, int maxBytes, CancellationToken token)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, token);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length < 1 || length > maxBytes) throw new InvalidDataException("Invalid screen-time message length.");
        var payload = new byte[length];
        try
        {
            await stream.ReadExactlyAsync(payload, token);
            return JsonSerializer.Deserialize<T>(payload, Json) ?? throw new InvalidDataException("Empty screen-time message.");
        }
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(payload); }
    }
}
