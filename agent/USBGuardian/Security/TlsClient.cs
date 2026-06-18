// ============================================================
// TlsClient.cs
// HttpClient pro komunikaci s API. Tři režimy ověření serveru:
//   1) pinnedThumbprint != "" → akceptuje JEN cert s daným otiskem
//      (self-contained TLS bez CA: šifrované + ověřená identita)
//   2) validateServerCertificate = false → akceptuje jakýkoli cert
//      (šifrované, NEověřené – jen pro vývoj)
//   3) jinak → výchozí validace OS (cert z CA, které stroj věří)
// ============================================================

namespace USBGuardian.Security;

public static class TlsClient
{
    public static HttpClient Create(bool validateServerCertificate, string? pinnedThumbprint, int timeoutSeconds)
    {
        var handler = new HttpClientHandler { UseDefaultCredentials = true };

        var pin = (pinnedThumbprint ?? string.Empty).Replace(" ", "").Replace(":", "").Trim();

        if (pin.Length > 0)
        {
            // Pinning – důvěra podle otisku, ne podle řetězce/CA
            handler.ServerCertificateCustomValidationCallback = (_, cert, _, _) =>
                cert != null &&
                string.Equals(cert.GetCertHashString(), pin, StringComparison.OrdinalIgnoreCase);
        }
        else if (!validateServerCertificate)
        {
            handler.ServerCertificateCustomValidationCallback =
                HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
        }
        // jinak: null = výchozí validace OS

        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(timeoutSeconds) };
    }
}
