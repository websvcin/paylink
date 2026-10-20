using MySqlConnector;
using WebSvc.PayLink.Storage;
using WebSvc.PayLink.Storage.Provisioning;

namespace PayLink.Pages;

/// <summary>A MySQL database entered as separate fields or as a pasted connection string.</summary>
public sealed class MySqlInput
{
    public bool UseConnectionString { get; set; }
    public string? ConnectionString { get; set; }
    public string? Host { get; set; }
    public uint Port { get; set; } = 3306;
    public string? Database { get; set; }
    public string? User { get; set; }
    public string? Password { get; set; }
    public MySqlSslMode SslMode { get; set; } = MySqlSslMode.Preferred;

    public bool TryBuild(out DatabaseTarget? target, out string error)
    {
        target = null;
        if (UseConnectionString)
        {
            if (string.IsNullOrWhiteSpace(ConnectionString)) { error = "Paste a MySQL connection string."; return false; }
            if (!MySqlConnectionDetails.TryParse(ConnectionString, out var parsed, out error)) return false;
            target = DatabaseTarget.ForMySql(parsed!);
            return true;
        }
        if (string.IsNullOrWhiteSpace(Host) || string.IsNullOrWhiteSpace(Database) || string.IsNullOrWhiteSpace(User))
        {
            error = "Enter the host, database name and user.";
            return false;
        }
        target = DatabaseTarget.ForMySql(new MySqlConnectionDetails(Host.Trim(), Port == 0 ? 3306 : Port, Database.Trim(), User.Trim(), Password ?? "", SslMode));
        error = "";
        return true;
    }
}

/// <summary>The database hosting form. Blank secret fields keep the saved value.</summary>
public sealed class HostingInput
{
    public HostingKind Kind { get; set; }
    public string? ConnectionHostOverride { get; set; }

    public string? MySqlHost { get; set; }
    public uint MySqlPort { get; set; } = 3306;
    public string? MySqlAdminUser { get; set; }
    public string? MySqlAdminPassword { get; set; }
    public MySqlSslMode MySqlSslMode { get; set; } = MySqlSslMode.Preferred;
    public string MySqlUserHost { get; set; } = "%";

    public string? PleskHost { get; set; }
    public int PleskPort { get; set; } = 8443;
    public int PleskSubscriptionId { get; set; }
    public string? PleskLogin { get; set; }
    public string? PleskPassword { get; set; }
    public string? PleskSecretKey { get; set; }

    public string? CPanelHost { get; set; }
    public int CPanelPort { get; set; } = 2083;
    public string? CPanelUsername { get; set; }
    public string? CPanelApiToken { get; set; }

    public static HostingInput From(HostingSettings s) => new()
    {
        Kind = s.Kind,
        ConnectionHostOverride = s.ConnectionHostOverride,
        MySqlHost = s.MySqlServer?.Host, MySqlPort = s.MySqlServer?.Port ?? 3306, MySqlAdminUser = s.MySqlServer?.AdminUser,
        MySqlSslMode = s.MySqlServer?.SslMode ?? MySqlSslMode.Preferred, MySqlUserHost = s.MySqlServer?.UserHost ?? "%",
        PleskHost = s.Plesk?.Host, PleskPort = s.Plesk?.Port ?? 8443, PleskSubscriptionId = s.Plesk?.SubscriptionId ?? 0, PleskLogin = s.Plesk?.Login,
        CPanelHost = s.CPanel?.Host, CPanelPort = s.CPanel?.Port ?? 2083, CPanelUsername = s.CPanel?.Username,
    };

    public HostingSettings ToSettings(HostingSettings saved)
    {
        static string Keep(string? input, string? saved) => string.IsNullOrEmpty(input) ? saved ?? "" : input;
        return new HostingSettings
        {
            Kind = Kind,
            ConnectionHostOverride = string.IsNullOrWhiteSpace(ConnectionHostOverride) ? null : ConnectionHostOverride.Trim(),
            MySqlServer = Kind != HostingKind.MySqlServer ? null : new MySqlServerHosting
            {
                Host = (MySqlHost ?? "").Trim(), Port = MySqlPort == 0 ? 3306 : MySqlPort, AdminUser = (MySqlAdminUser ?? "").Trim(),
                AdminPassword = Keep(MySqlAdminPassword, saved.MySqlServer?.AdminPassword), SslMode = MySqlSslMode,
                UserHost = string.IsNullOrWhiteSpace(MySqlUserHost) ? "%" : MySqlUserHost.Trim(),
            },
            Plesk = Kind != HostingKind.Plesk ? null : new PleskHosting
            {
                Host = (PleskHost ?? "").Trim(), Port = PleskPort == 0 ? 8443 : PleskPort, SubscriptionId = PleskSubscriptionId,
                Login = PleskLogin?.Trim(), Password = Keep(PleskPassword, saved.Plesk?.Password), SecretKey = Keep(PleskSecretKey, saved.Plesk?.SecretKey),
            },
            CPanel = Kind != HostingKind.CPanel ? null : new CPanelHosting
            {
                Host = (CPanelHost ?? "").Trim(), Port = CPanelPort == 0 ? 2083 : CPanelPort, Username = (CPanelUsername ?? "").Trim(),
                ApiToken = Keep(CPanelApiToken, saved.CPanel?.ApiToken),
            },
        };
    }

    public string? Validate() => Kind switch
    {
        HostingKind.MySqlServer when string.IsNullOrWhiteSpace(MySqlHost) || string.IsNullOrWhiteSpace(MySqlAdminUser) => "Enter the MySQL host and an admin user.",
        HostingKind.Plesk when string.IsNullOrWhiteSpace(PleskHost) || PleskSubscriptionId <= 0 => "Enter the Plesk host and subscription ID.",
        HostingKind.CPanel when string.IsNullOrWhiteSpace(CPanelHost) || string.IsNullOrWhiteSpace(CPanelUsername) => "Enter the cPanel host and account user name.",
        _ => null,
    };
}
