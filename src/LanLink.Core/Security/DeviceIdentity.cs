using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace LanLink.Core.Security;

/// <summary>Certificat TLS auto-signé de ce PC (ECDSA P-256) et son empreinte SHA-256.</summary>
public sealed class DeviceIdentity : IDisposable
{
    public X509Certificate2 Certificate { get; }
    public byte[] Fingerprint { get; }
    public string FingerprintHex => Convert.ToHexString(Fingerprint);

    public DeviceIdentity(X509Certificate2 certificate)
    {
        Certificate = certificate;
        Fingerprint = SHA256.HashData(certificate.RawData);
    }

    public static DeviceIdentity Create()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var req = new CertificateRequest($"CN=LanLink-{Guid.NewGuid():N}", key, HashAlgorithmName.SHA256);
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection
        {
            new Oid("1.3.6.1.5.5.7.3.1"), // serverAuth
            new Oid("1.3.6.1.5.5.7.3.2"), // clientAuth
        }, false));
        using var selfSigned = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(10));
        return FromPfx(selfSigned.Export(X509ContentType.Pfx));
    }

    /// <summary>Recharge un PFX ; ce ré-import est nécessaire sous Windows pour que SslStream puisse utiliser la clé.</summary>
    public static DeviceIdentity FromPfx(byte[] pfx) =>
        new(new X509Certificate2(pfx, (string?)null, X509KeyStorageFlags.Exportable));

    public byte[] ExportPfx() => Certificate.Export(X509ContentType.Pfx);

    public void Dispose() => Certificate.Dispose();
}
