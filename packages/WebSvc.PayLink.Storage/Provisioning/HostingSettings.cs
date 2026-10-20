using MySqlConnector;

namespace WebSvc.PayLink.Storage.Provisioning;

public enum HostingKind { None, MySqlServer, Plesk, CPanel }

/// <summary>
/// The one hosting environment PayLink may use to create MySQL databases on demand.
/// Stored encrypted by the host application; never sent back to a browser.
/// </summary>
public sealed class HostingSettings
{
    public HostingKind Kind { get; set; } = HostingKind.None;
    public MySqlServerHosting? MySqlServer { get; set; }
    public PleskHosting? Plesk { get; set; }
    public CPanelHosting? CPanel { get; set; }

    /// <summary>Optional: the host name applications use to reach created databases, when it differs from the panel host.</summary>
    public string? ConnectionHostOverride { get; set; }

    public string Describe() => Kind switch
    {
        HostingKind.MySqlServer => $"MySQL server · {MySqlServer?.Host}:{MySqlServer?.Port}",
        HostingKind.Plesk => $"Plesk · {Plesk?.Host} · subscription {Plesk?.SubscriptionId}",
        HostingKind.CPanel => $"cPanel · {CPanel?.Host} · account {CPanel?.Username}",
        _ => "Not set up",
    };
}

public sealed class MySqlServerHosting
{
    public string Host { get; set; } = "";
    public uint Port { get; set; } = 3306;
    /// <summary>An account that can CREATE DATABASE, CREATE USER and GRANT. Not used by tenants day to day.</summary>
    public string AdminUser { get; set; } = "";
    public string AdminPassword { get; set; } = "";
    public MySqlSslMode SslMode { get; set; } = MySqlSslMode.Preferred;
    /// <summary>Host part of created users, e.g. "%" (any host) or the PayLink server's address.</summary>
    public string UserHost { get; set; } = "%";
}

public sealed class PleskHosting
{
    public string Host { get; set; } = "";
    public int Port { get; set; } = 8443;
    public int SubscriptionId { get; set; }
    /// <summary>Either an admin login and password, or a secret key generated over SSH.</summary>
    public string? Login { get; set; }
    public string? Password { get; set; }
    public string? SecretKey { get; set; }
}

public sealed class CPanelHosting
{
    public string Host { get; set; } = "";
    public int Port { get; set; } = 2083;
    public string Username { get; set; } = "";
    public string ApiToken { get; set; } = "";
}
