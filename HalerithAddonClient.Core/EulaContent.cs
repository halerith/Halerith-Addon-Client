namespace HalerithAddonClient.Core;

/// <summary>
/// Baked-in EULA text for the Halerith Addon Client.
/// The published database document must contain this exact HTML so online and
/// offline users are asked to accept the same agreement and content hash.
/// </summary>
public static class EulaContent
{
    public const string Version = "3.0.0";

    public const string EffectiveDate = "2026-11-02";

    public static string NormalizedHtml => Html.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Trim();

    public const string Html = @"<h2>Halerith Addon Client Agreement</h2>
<p><strong>Version 3.0.0 — effective November 2, 2026</strong></p>

<h3>1. Scope and acceptance</h3>
<p>This Agreement governs your use of the Halerith Addon Client (the “Client”), desktop software provided by Daniel Grandja, a sole proprietor carrying on business as Halerith (“Halerith”, “we”, “us”, or “our”). By selecting “I Accept” in the Client, you agree to this Agreement. If you do not agree, close the Client and uninstall it.</p>
<p>You can use local Client features and available Trial or Free managed server access without creating a Halerith account. The separately presented <a href=""https://halerith.com/products/addon/platform/agreement.php"">Halerith Addon Platform Agreement</a> governs use of the online Platform through a Halerith account, including its Manager functions, Client-facing account services and subscriptions. The <a href=""https://halerith.com/privacy/"">Privacy Policy</a> explains how personal information is handled.</p>
<p>You must be at least 13 to use the Client. If you cannot legally agree on your own, a parent or guardian must authorize your use and, where required, agree on your behalf. If you use the Client for an organization, you must have authority to do so.</p>

<h3>2. Using the Client</h3>
<p>You may install and use the official Client on devices you own or are authorized to use. Halerith names, logos and trademarks may not be used as your own branding unless separately permitted.</p>

<h3>3. Your use of the Client</h3>
<p>Use the Client only with accounts, devices, servers and content you are authorized to use. Do not impersonate another person, forge credentials or use the Client to access a service or another person's data without permission. You are responsible for the content you choose to install or provide and for complying with its applicable licence.</p>
<p>Minecraft, Microsoft, Xbox, content providers and server hosts are independent services with their own terms. This Agreement does not grant you rights in their accounts, software, servers or content. Managed server access also requires current permission for the selected Minecraft account and the server.</p>
<p>An eligible Minecraft account has one 30-day Trial, beginning when Halerith first returns an authorized managed manifest containing content for that account. During the Trial, it can use all servers for which it has current managed access. Afterwards, Free managed access is limited to one eligible server assigned by Halerith. A Halerith account is needed to manage a Free server change in the Addon portal. Reinstalling the Client or linking the Minecraft account to a Halerith account does not restart its Trial or erase its Free history.</p>

<h3>4. Changes you request on your device</h3>
<p>When you ask the Client to install, synchronize, update or remove content, manage an instance, or launch Minecraft, you authorize it to make the corresponding changes to files and settings on your device. Those changes may affect an existing installation and may remain after you uninstall the Client. Review your choices and keep independent copies of worlds, instances, custom files and settings that matter to you.</p>

<h3>5. Updates and compatibility</h3>
<p>Halerith may offer Client updates. Access to a hosted feature may require a compatible Client version. We do not promise that every Client release will work with every device, Minecraft version, mod, loader or independent service.</p>

<h3>6. Privacy and optional diagnostics</h3>
<p>The Client stores the Minecraft accounts you add, including their names, UUIDs and protected sign-in credentials, along with Client settings and local content information on your device. For managed access, Halerith temporarily receives the selected Minecraft account's access token to verify its identity and records that UUID's server permissions, Trial and Free history, and any linked subscription grant, even if you have no Halerith account or email address. Removing an account from the Client does not erase its server-side Trial or Free history.</p>
<p>Sending a diagnostic report is optional. If you choose to send one, Halerith receives the selected Client information, logs and any notes or attachments you include to investigate the issue; these may contain personal information. The <a href=""https://halerith.com/privacy/"">Privacy Policy</a> explains the information Halerith receives, uses, shares and retains, and how to make a privacy request. Accepting this Agreement does not authorize unrelated collection, sharing or marketing.</p>

<h3>7. Availability and legal rights</h3>
<p>To the extent permitted by law, the Client is provided without a promise of uninterrupted operation or universal compatibility. Nothing in this Agreement excludes a consumer guarantee, statutory remedy, privacy right or liability that cannot lawfully be excluded.</p>

<h3>8. Changes and ending use</h3>
<p>We may publish a new version of this Agreement and ask for affirmative acceptance before continued use of an affected Client release. You may stop using and uninstall the Client at any time. Uninstalling the Client does not itself cancel a subscription or delete your Halerith account; those are managed through the Addon portal.</p>

<h3>9. Contact</h3>
<p>Client questions: <a href=""mailto:info@halerith.com"">info@halerith.com</a>. Privacy requests: <a href=""mailto:privacy@halerith.com"">privacy@halerith.com</a>. Account and subscription management: <a href=""https://addon.halerith.com/"">Addon portal</a>.</p>";
}
