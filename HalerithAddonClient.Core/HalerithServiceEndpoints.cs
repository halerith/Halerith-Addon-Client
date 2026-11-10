namespace HalerithAddonClient.Core;

/// <summary>
/// Canonical Halerith service endpoints used by the Desktop Client.
/// Product APIs live on the Client host; release discovery lives on the Update host.
/// </summary>
public static class HalerithServiceEndpoints
{
    public const string ClientApiBaseUrl = "https://client.halerith.com/v1";
    public const string UpdateApiBaseUrl = "https://update.halerith.com/v1/client";

    public static class Client
    {
        public const string ReportsPath = "/reports";
        public const string EulaPath = "/eula";
        public const string PrivacyPath = "/privacy";

        public const string EulaUrl = ClientApiBaseUrl + EulaPath;
        public const string PrivacyUrl = ClientApiBaseUrl + PrivacyPath;
    }

    public static class Update
    {
        public const string CheckUrl = UpdateApiBaseUrl + "/update-check";
        public const string AppcastUrl = UpdateApiBaseUrl + "/appcast";
    }
}
