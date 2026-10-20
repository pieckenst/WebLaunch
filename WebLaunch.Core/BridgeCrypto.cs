using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WebLaunch.Core;

public sealed record ClientHello(int Version, string Identity, string Ephemeral, string Nonce, string Signature);
public sealed record ServerHello(string SessionId, string Identity, string Ephemeral, string Nonce, string Signature, bool KnownBrowser);
public sealed record EncryptedMessage(long Sequence, string Ciphertext);

public static class BridgeCrypto
{
    public static string ClientTranscript(string origin, ClientHello hello) =>
        $"weblaunch-v2\n{origin}\n{hello.Identity}\n{hello.Ephemeral}\n{hello.Nonce}";
    public static string Transcript(string origin, ClientHello client, ServerHello server) =>
        $"{ClientTranscript(origin, client)}\n{server.Identity}\n{server.Ephemeral}\n{server.Nonce}\n{server.SessionId}";
    public static string Fingerprint(string identity) => Convert.ToHexString(SHA256.HashData(Convert.FromBase64String(identity)));
    public static string PairingCode(string transcript) =>
        (BinaryPrimitives.ReadUInt32BigEndian(SHA256.HashData(Encoding.UTF8.GetBytes(transcript))) % 1_000_000).ToString("D6");
    public static string Sign(ECDsa identity, string transcript) => Convert.ToBase64String(identity.SignData(
        Encoding.UTF8.GetBytes(transcript), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
    public static bool Verify(string publicKey, string transcript, string signature)
    {
        try
        {
            using var identity = ECDsa.Create();
            var bytes = Convert.FromBase64String(publicKey);
            identity.ImportSubjectPublicKeyInfo(bytes, out var read);
            return read == bytes.Length && identity.ExportParameters(false).Curve.Oid.Value == ECCurve.NamedCurves.nistP256.Oid.Value &&
                identity.VerifyData(Encoding.UTF8.GetBytes(transcript), Convert.FromBase64String(signature), HashAlgorithmName.SHA256,
                    DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException) { return false; }
    }
    public static void ValidateHello(string origin, ClientHello hello)
    {
        if (hello.Identity is null || hello.Ephemeral is null || hello.Nonce is null || hello.Signature is null || hello.Version != 2 || hello.Identity.Length > 256 || hello.Ephemeral.Length > 256 || hello.Nonce.Length != 44 ||
            hello.Signature.Length > 128 || Convert.FromBase64String(hello.Nonce).Length != 32 ||
            !Verify(hello.Identity, ClientTranscript(origin, hello), hello.Signature))
            throw new CryptographicException("Invalid handshake.");
    }
}

public sealed class SecureChannel : IDisposable
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly byte[] incomingKey;
    private readonly byte[] outgoingKey;
    private readonly string transcriptHash;
    private readonly string incomingDirection;
    private readonly string outgoingDirection;
    private long received;
    private long sent;

    public SecureChannel(byte[] sharedSecret, string transcript, bool desktop)
    {
        var salt = SHA256.HashData(Encoding.UTF8.GetBytes(transcript));
        transcriptHash = Convert.ToBase64String(salt);
        incomingDirection = desktop ? "client" : "desktop";
        outgoingDirection = desktop ? "desktop" : "client";
        incomingKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, sharedSecret, 32, salt, Encoding.UTF8.GetBytes("weblaunch-v2/" + incomingDirection));
        outgoingKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, sharedSecret, 32, salt, Encoding.UTF8.GetBytes("weblaunch-v2/" + outgoingDirection));
    }
    private byte[] Aad(string direction, long sequence) => Encoding.UTF8.GetBytes($"{transcriptHash}\n{direction}\n{sequence}");
    private static byte[] Nonce(long sequence)
    {
        var nonce = new byte[12];
        BinaryPrimitives.WriteInt64BigEndian(nonce.AsSpan(4), sequence);
        return nonce;
    }
    public EncryptedMessage Encrypt<T>(T value)
    {
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        try
        {
            var sequence = checked(++sent);
            var bytes = new byte[plaintext.Length + 16];
            using var aes = new AesGcm(outgoingKey, 16);
            aes.Encrypt(Nonce(sequence), plaintext, bytes.AsSpan(0, plaintext.Length), bytes.AsSpan(plaintext.Length), Aad(outgoingDirection, sequence));
            return new(sequence, Convert.ToBase64String(bytes));
        }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }
    public T Decrypt<T>(EncryptedMessage message)
    {
        if (message.Sequence != received + 1 || message.Ciphertext is null || message.Ciphertext.Length > 60_000)
            throw new CryptographicException("Invalid message sequence or size.");
        var bytes = Convert.FromBase64String(message.Ciphertext);
        if (bytes.Length < 16) throw new CryptographicException("Invalid message.");
        var plaintext = new byte[bytes.Length - 16];
        try
        {
            using var aes = new AesGcm(incomingKey, 16);
            aes.Decrypt(Nonce(message.Sequence), bytes.AsSpan(0, plaintext.Length), bytes.AsSpan(plaintext.Length), plaintext, Aad(incomingDirection, message.Sequence));
            var result = JsonSerializer.Deserialize<T>(plaintext, Json) ?? throw new InvalidDataException("Invalid message.");
            received = message.Sequence;
            return result;
        }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }
    public void Dispose()
    {
        CryptographicOperations.ZeroMemory(incomingKey);
        CryptographicOperations.ZeroMemory(outgoingKey);
    }
}
