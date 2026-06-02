using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

public static class CertificateFactory
{
    public static X509Certificate2 CreateLocalhostCertificate()
    {
        using RSA rsa = RSA.Create(2048);
        CertificateRequest request = new CertificateRequest(
            "CN=127.0.0.1",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        request.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, false));
        request.CertificateExtensions.Add(
            new X509SubjectKeyIdentifierExtension(request.PublicKey, false));

        SubjectAlternativeNameBuilder names = new SubjectAlternativeNameBuilder();
        names.AddIpAddress(IPAddress.Loopback);
        names.AddDnsName("localhost");
        request.CertificateExtensions.Add(names.Build());

        DateTimeOffset start = DateTimeOffset.UtcNow.AddDays(-1);
        DateTimeOffset end = DateTimeOffset.UtcNow.AddDays(30);
        using X509Certificate2 certificate = request.CreateSelfSigned(start, end);

        return X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pfx), null);
    }
}
