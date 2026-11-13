namespace RelaxKon.Auth.Security.PasswordReset;

public sealed class PasswordResetOptions
{
    public bool Enabled { get; set; }
    public string PublicBaseUrl { get; set; } = "";
    public string SmtpHost { get; set; } = "";
    public int SmtpPort { get; set; } = 587;
    public bool EnableSsl { get; set; } = true;
    public string FromAddress { get; set; } = "";
    public string SmtpUsername { get; set; } = "";
    public string SmtpPassword { get; set; } = "";

    public bool IsValid(bool allowPrivateHttp = false) => !Enabled || (
        Uri.TryCreate(PublicBaseUrl, UriKind.Absolute, out var uri) &&
        (uri.Scheme == "https" || uri.Scheme == "http" && (uri.IsLoopback || allowPrivateHttp && IsPrivateIpv4(uri.Host))) &&
        string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment) && string.IsNullOrEmpty(uri.UserInfo) &&
        IsSmtpValid());

    private static bool IsPrivateIpv4(string host)
    {
        if (!System.Net.IPAddress.TryParse(host, out var address)) return false;
        return address.GetAddressBytes() is [10, _, _, _] or [172, >= 16 and <= 31, _, _] or [192, 168, _, _];
    }

    public bool IsSmtpValid() => !string.IsNullOrWhiteSpace(SmtpHost) && SmtpPort is > 0 and <= 65535 &&
        (SmtpPort != 465 || EnableSsl) &&
        System.Net.Mail.MailAddress.TryCreate(FromAddress, out _);
}
