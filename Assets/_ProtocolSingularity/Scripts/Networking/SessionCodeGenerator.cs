using System;
using System.Text;

namespace ProtocolSingularity.Networking
{
    /// <summary>
    /// セッションコード生成。人が読みやすい英数字のみ（0/O, 1/I/L 等の紛らわしい文字除外）。
    /// </summary>
    public static class SessionCodeGenerator
    {
        private const string Chars = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";

        public static string Generate(int length = 6)
        {
            if (length < 1) length = 1;
            var rng = new Random();
            var sb = new StringBuilder(length);
            for (int i = 0; i < length; i++)
                sb.Append(Chars[rng.Next(Chars.Length)]);
            return sb.ToString();
        }
    }
}
