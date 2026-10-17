using System.Text.RegularExpressions;

namespace CarsControl.Setup
{
    /// <summary>
    /// A release's notes are Markdown (they're the CHANGELOG section). The app shows them in a plain text box, so the
    /// markers that would only get in the way are removed: bold and code markers, link addresses, heading hashes, and
    /// list dashes become bullets.
    /// </summary>
    public static class ReleaseNotesText
    {
        private static readonly Regex Link = new Regex(@"\[(?<text>[^\]]+)\]\((?<url>[^)\s]+)\)", RegexOptions.Compiled);
        private static readonly Regex Bold = new Regex(@"\*\*(?<t>.+?)\*\*", RegexOptions.Compiled);
        private static readonly Regex Italic = new Regex(@"(?<![\*\w])\*(?<t>[^\*\s][^\*]*?)\*(?![\*\w])", RegexOptions.Compiled);
        private static readonly Regex Code = new Regex(@"`(?<t>[^`]+)`", RegexOptions.Compiled);
        private static readonly Regex Heading = new Regex(@"^#{1,6}\s+", RegexOptions.Compiled);
        private static readonly Regex Bullet = new Regex(@"^(?<indent>\s*)[-*]\s+", RegexOptions.Compiled);

        public static string Plain(string? markdown)
        {
            if (string.IsNullOrWhiteSpace(markdown)) return "";
            var lines = new System.Collections.Generic.List<string>();
            foreach (var raw in markdown!.Replace("\r\n", "\n").Split('\n'))
            {
                var line = raw.TrimEnd();
                line = Link.Replace(line, "${text}");
                line = Bold.Replace(line, "${t}");
                line = Code.Replace(line, "${t}");
                line = Italic.Replace(line, "${t}");
                line = Heading.Replace(line, "");
                line = Bullet.Replace(line, m => m.Groups["indent"].Value + "• ");
                lines.Add(line);
            }
            // At most one blank line in a row, none at the ends.
            var result = new System.Text.StringBuilder();
            var blank = 0;
            foreach (var line in lines)
            {
                if (line.Length == 0)
                {
                    blank++;
                    if (blank > 1) continue;
                }
                else blank = 0;
                result.Append(line).Append('\n');
            }
            return result.ToString().Trim();
        }
    }
}
