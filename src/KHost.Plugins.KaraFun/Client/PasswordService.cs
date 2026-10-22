using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;

namespace KHost.Plugins.KaraFun.Client;

/// <summary>Reproduces <c>KaraFunNetwork.PasswordService.hash(password:)</c>: pwd is lowercase-hex
/// MD5 of a fixed monoalphabetic substitution, recovered by reverse engineering, not a salt or key.</summary>
public static class PasswordService
{
    // Aligned alphabets: IN[i] -> OUT[i]. Non-alphanumeric characters are passed through.
    private const string IN = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";
    private const string OUT = "5rfOTGCB4bg76UJaSQF80to1hiDmREwslePn9NpWXkjLKIuz2ZxyHVYqc3vdMA";

    /// <summary>The character substitution applied before MD5 (alphanumerics only).</summary>
    public static string Substitute(string password)
    {
        var sb = new StringBuilder(password.Length);
        foreach (char c in password)
        {
            int i = IN.IndexOf(c);
            sb.Append(i >= 0 ? OUT[i] : c);
        }
        return sb.ToString();
    }

    /// <summary>The <c>pwd</c> value: lowercase-hex MD5 of the substituted password.</summary>
    [SuppressMessage("Security", "CA5351:Do Not Use Broken Cryptographic Algorithms",
        Justification =
            "The algorithm is not ours to pick: session/open rejects anything else, so the " +
            "alternative to this hash is no sign-in at all. It protects nothing here either — " +
            "the password is already in memory, and what crosses the wire is the hash the " +
            "official client sends. Revisit only if the API accepts something stronger.")]
    public static string Hash(string password)
        => Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(Substitute(password))));
}
