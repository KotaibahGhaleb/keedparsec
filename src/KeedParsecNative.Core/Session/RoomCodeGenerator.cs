using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace KeedParsecNative.Core.Session;

/// <summary>
/// Generates cryptographically random room codes using an unambiguous alphabet
/// (no 0/O, 1/I/L). Codes are identifiers, not credentials.
/// </summary>
public static class RoomCodeGenerator
{
    private const string Alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";

    public static string Generate(int length = 6)
    {
        if (length < 4 || length > 12)
        {
            throw new ArgumentOutOfRangeException(nameof(length), "Room code length must be 4-12.");
        }

        var chars = new char[length];
        var bytes = new byte[length];
        RandomNumberGenerator.Fill(bytes);
        for (var i = 0; i < length; i++)
        {
            chars[i] = Alphabet[bytes[i] % Alphabet.Length];
        }
        return new string(chars);
    }

    public static bool IsValidFormat(string? roomCode, int length = 6)
    {
        if (roomCode is null || roomCode.Length != length)
        {
            return false;
        }
        foreach (var c in roomCode)
        {
            if (!Alphabet.Contains(char.ToUpperInvariant(c)))
            {
                return false;
            }
        }
        return true;
    }
}

/// <summary>
/// Issues and validates short-lived HMAC-signed join tokens. The room code
/// alone is never sufficient to join; the host must approve or a valid token
/// must be presented.
/// </summary>
public sealed class JoinTokenService
{
    private readonly byte[] _key;
    private readonly ConcurrentDictionary<string, long> _consumedTokens = new();

    public JoinTokenService(byte[] key)
    {
        _key = key.Length >= 32 ? key : SHA256.HashData(key);
    }

    /// <summary>Creates a token for the given room, valid for ttlSeconds.</summary>
    public string CreateToken(string roomId, string? peerId, TimeSpan ttl)
    {
        var expiresAtTicks = DateTimeOffset.UtcNow.Add(ttl).Ticks;
        var nonceBytes = new byte[8];
        RandomNumberGenerator.Fill(nonceBytes);
        var nonce = Convert.ToHexString(nonceBytes);

        var payload = $"{roomId}|{peerId ?? string.Empty}|{expiresAtTicks}|{nonce}";
        var signature = Sign(payload);
        var tokenPayload = $"{Convert.ToBase64String(Encoding.UTF8.GetBytes(payload))}.{signature}";
        return tokenPayload;
    }

    /// <summary>Validates a token for the given room. Returns false if expired, tampered, or room mismatch.</summary>
    public bool ValidateToken(string roomId, string token) => TryValidate(roomId, token, consume: false).IsValid;

    /// <summary>
    /// Validates a token and consumes it so it cannot be replayed. Returns false
    /// for invalid, expired, or already-consumed tokens.
    /// </summary>
    public bool ConsumeToken(string roomId, string token)
    {
        var result = TryValidate(roomId, token, consume: true);
        return result.IsValid;
    }

    private (bool IsValid, long ExpiresAtTicks) TryValidate(string roomId, string token, bool consume)
    {
        var dot = token.LastIndexOf('.');
        if (dot <= 0)
        {
            return (false, 0);
        }

        try
        {
            var payloadPart = token[..dot];
            var signaturePart = token[(dot + 1)..];
            var payloadBytes = Convert.FromBase64String(payloadPart);
            var payload = Encoding.UTF8.GetString(payloadBytes);
            var parts = payload.Split('|');
            if (parts.Length != 4 || parts[0] != roomId)
            {
                return (false, 0);
            }

            // Verify signature before trusting contents.
            var expected = Sign(payload);
            if (!CryptographicOperations.FixedTimeEquals(
                    Convert.FromHexString(signaturePart),
                    Convert.FromHexString(expected)))
            {
                return (false, 0);
            }

            if (!long.TryParse(parts[2], out var expiresAtTicks) ||
                DateTimeOffset.UtcNow.Ticks >= expiresAtTicks)
            {
                return (false, 0);
            }

            if (consume)
            {
                // Single-use: record the token signature until its expiry.
                if (!_consumedTokens.TryAdd(payloadPart, expiresAtTicks))
                {
                    return (false, 0);
                }
                // Opportunistic cleanup of expired entries.
                if (_consumedTokens.Count > 256)
                {
                    var now = DateTimeOffset.UtcNow.Ticks;
                    foreach (var kv in _consumedTokens)
                    {
                        if (kv.Value < now)
                        {
                            _consumedTokens.TryRemove(kv.Key, out _);
                        }
                    }
                }
            }

            return (true, expiresAtTicks);
        }
        catch (FormatException)
        {
            return (false, 0);
        }
        catch (ArgumentException)
        {
            return (false, 0);
        }
    }

    private string Sign(string payload)
    {
        using var hmac = new HMACSHA256(_key);
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(payload)));
    }
}
