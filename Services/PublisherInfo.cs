using System.Diagnostics;
using System.IO;
using System.Security.Cryptography.X509Certificates;

namespace BgOptimizer.Services;

/// <summary>
/// Who a program says made it, as a hint for deciding whether to keep it at startup. Read from the signature
/// embedded in the file (the signer's name) and, failing that, the Company field of its version info.
/// It is NOT a verification: the signature is not validated and the version text is whatever the file claims,
/// so treat it as a label to recognise a program by, never as proof that the file is genuine.
/// Files are only opened to read this metadata; nothing is ever run.
/// </summary>
internal static class PublisherInfo
{
    public static string? Get(string? path)
    {
        // The path is untrusted text from the registry or a shortcut: only a file on a local fixed drive is opened,
        // never a network path (which would contact that server) or a removable drive.
        if (!PathProbe.IsOnLocalFixedDrive(path)) return null;
        try
        {
            if (!File.Exists(path)) return null;
            var signer = SignerName(path);
            if (signer != null) return signer;
            var company = FileVersionInfo.GetVersionInfo(path).CompanyName;
            return string.IsNullOrWhiteSpace(company) ? null : company.Trim();
        }
        catch { return null; }
    }

    private static string? SignerName(string path)
    {
        try
        {
            // SYSLIB0057 steers certificate loading to X509CertificateLoader, but that reads standalone certificate
            // files; extracting the signer from a signed program is still only possible through this call.
#pragma warning disable SYSLIB0057
            using var certificate = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
#pragma warning restore SYSLIB0057
            var name = certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false);
            return string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        }
        catch { return null; } // unsigned, or signed through a Windows catalog instead of inside the file
    }
}
