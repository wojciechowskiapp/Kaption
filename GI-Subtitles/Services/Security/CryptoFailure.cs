using System;
using System.Security.Cryptography;

namespace GI_Subtitles.Services.Security
{
    /// <summary>
    /// Recognises a protected file that failed HMAC verification: encrypted under another
    /// device key or machine fingerprint, or tampered with. Re-downloading it is the fix.
    /// </summary>
    public static class CryptoFailure
    {
        /// <summary>True when <paramref name="ex"/> or any exception in its inner chain is a
        /// <see cref="CryptographicException"/>.</summary>
        public static bool IsCryptographic(Exception ex)
        {
            for (Exception cur = ex; cur != null; cur = cur.InnerException)
            {
                if (cur is CryptographicException)
                    return true;
            }
            return false;
        }
    }
}
