using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace NameToWin
{
    class Program
    {
        const string AppName = "name2win";
        const string AppVersion = "2.1.0";

        static bool recursive = false;
        static bool dryRun = false;
        static bool forceMode = false;
        static bool checkMode = false;
        static bool applyAll = false;

        static int filesRenamed = 0;
        static int filesMerged = 0;
        static int filesSkipped = 0;
        static int dirsRenamed = 0;

        // 外部マッピング対応表 (変換元, 変換先)
        static List<KeyValuePair<string, string>> customMappings = new List<KeyValuePair<string, string>>();
        static string loadedMappingFilePath = null;

        // Shift_JIS (CP932)
        static readonly Encoding ShiftJis = Encoding.GetEncoding(932);

        static void Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;
            LoadCustomMappings();

            // 引数なし起動時はヘルプと使用方法を表示して終了
            if (args.Length == 0)
            {
                ShowHelp();
                return;
            }

            List<string> targets = new List<string>();

            // 引数オプション解析
            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];
                if (arg.Equals("-r", StringComparison.OrdinalIgnoreCase) ||
                    arg.Equals("--recursive", StringComparison.OrdinalIgnoreCase))
                {
                    recursive = true;
                }
                else if (arg.Equals("-t", StringComparison.OrdinalIgnoreCase) ||
                         arg.Equals("--test", StringComparison.OrdinalIgnoreCase) ||
                         arg.Equals("--dry-run", StringComparison.OrdinalIgnoreCase))
                {
                    dryRun = true;
                }
                else if (arg.Equals("-f", StringComparison.OrdinalIgnoreCase) ||
                         arg.Equals("--force", StringComparison.OrdinalIgnoreCase))
                {
                    forceMode = true;
                }
                else if (arg.Equals("-c", StringComparison.OrdinalIgnoreCase) ||
                         arg.Equals("--check", StringComparison.OrdinalIgnoreCase))
                {
                    checkMode = true;
                }
                else if (arg.Equals("-v", StringComparison.OrdinalIgnoreCase) ||
                         arg.Equals("--version", StringComparison.OrdinalIgnoreCase))
                {
                    ShowVersion();
                    return;
                }
                else if (arg.Equals("-h", StringComparison.OrdinalIgnoreCase) ||
                         arg.Equals("--help", StringComparison.OrdinalIgnoreCase) ||
                         arg.Equals("/?"))
                {
                    ShowHelp();
                    return;
                }
                else if (!arg.StartsWith("-"))
                {
                    targets.Add(arg);
                }
            }

            if (targets.Count == 0)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("エラー: 対象のファイル、フォルダー、または文字列が指定されていません。");
                Console.ResetColor();
                ShowHelp();
                return;
            }

            if (checkMode)
            {
                foreach (string target in targets)
                {
                    RunCheckDiagnostic(target);
                }
                return;
            }

            Console.WriteLine("==================================================");
            Console.WriteLine(string.Format("  {0} v{1} - Name to Windows (Pole to Win!)", AppName, AppVersion));
            Console.WriteLine("==================================================");
            Console.WriteLine(string.Format("再帰処理 (-r)  : {0}", recursive ? "有効" : "無効"));
            Console.WriteLine(string.Format("強制自動 (-f)  : {0}", forceMode ? "有効 (対話なし・自動解決)" : "無効 (衝突時に対話確認)"));
            Console.WriteLine(string.Format("テスト実行 (-t): {0}", dryRun ? "有効 (変更は適用されません)" : "無効 (実行)"));
            if (!string.IsNullOrEmpty(loadedMappingFilePath))
            {
                Console.WriteLine(string.Format("外部対応表     : 有効 ({0} - {1} 件のルール)", Path.GetFileName(loadedMappingFilePath), customMappings.Count));
            }
            Console.WriteLine("--------------------------------------------------");

            try
            {
                foreach (string target in targets)
                {
                    ProcessTarget(target);
                }

                Console.WriteLine("--------------------------------------------------");
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine(string.Format("完了: クレンジング {0} 件, 統合削除 {1} 件, スキップ {2} 件, フォルダー {3} 件{4}",
                    filesRenamed,
                    filesMerged,
                    filesSkipped,
                    dirsRenamed,
                    dryRun ? " (※テスト実行のため未保存)" : ""));
                Console.ResetColor();
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine(string.Format("予期しないエラーが発生しました: {0}", ex.Message));
                Console.ResetColor();
            }
        }

        // ====================================================================
        // クレンジングパイプライン（Shift_JIS & Windows完全適合）
        // ====================================================================
        static void PrintDiff(string oldStr, string newStr)
        {
            int m = oldStr.Length;
            int n = newStr.Length;
            int[,] dp = new int[m + 1, n + 1];

            for (int i = 1; i <= m; i++)
            {
                for (int j = 1; j <= n; j++)
                {
                    if (oldStr[i - 1] == newStr[j - 1])
                        dp[i, j] = dp[i - 1, j - 1] + 1;
                    else
                        dp[i, j] = Math.Max(dp[i - 1, j], dp[i, j - 1]);
                }
            }

            int currI = m, currJ = n;
            List<char> oChars = new List<char>();
            List<bool> oDiff = new List<bool>();
            List<char> nChars = new List<char>();
            List<bool> nDiff = new List<bool>();

            while (currI > 0 || currJ > 0)
            {
                if (currI > 0 && currJ > 0 && oldStr[currI - 1] == newStr[currJ - 1])
                {
                    oChars.Add(oldStr[currI - 1]); oDiff.Add(false);
                    nChars.Add(newStr[currJ - 1]); nDiff.Add(false);
                    currI--; currJ--;
                }
                else if (currJ > 0 && (currI == 0 || dp[currI, currJ - 1] >= dp[currI - 1, currJ]))
                {
                    nChars.Add(newStr[currJ - 1]); nDiff.Add(true);
                    currJ--;
                }
                else
                {
                    oChars.Add(oldStr[currI - 1]); oDiff.Add(true);
                    currI--;
                }
            }

            oChars.Reverse(); oDiff.Reverse();
            nChars.Reverse(); nDiff.Reverse();

            for (int i = 0; i < oChars.Count; i++)
            {
                if (oDiff[i]) Console.ForegroundColor = ConsoleColor.Red;
                else Console.ResetColor();
                Console.Write(oChars[i]);
            }
            Console.ResetColor();
            Console.Write(" -> ");

            for (int i = 0; i < nChars.Count; i++)
            {
                if (nDiff[i]) Console.ForegroundColor = ConsoleColor.Yellow;
                else Console.ResetColor();
                Console.Write(nChars[i]);
            }
            Console.ResetColor();
            Console.WriteLine();
        }

        static void LoadCustomMappings()
        {
            try
            {
                string baseDir = AppDomain.CurrentDomain.BaseDirectory ?? "";
                string currentDir = Environment.CurrentDirectory ?? "";

                string[] candidateFiles = new string[]
                {
                    Path.Combine(currentDir, "name2win.mapping.txt"),
                    Path.Combine(currentDir, "name2win.mapping.tsv"),
                    Path.Combine(baseDir, "name2win.mapping.txt"),
                    Path.Combine(baseDir, "name2win.mapping.tsv")
                };

                string targetFile = null;
                for (int i = 0; i < candidateFiles.Length; i++)
                {
                    if (File.Exists(candidateFiles[i]))
                    {
                        targetFile = candidateFiles[i];
                        break;
                    }
                }

                if (targetFile == null) return;

                string[] lines = File.ReadAllLines(targetFile, Encoding.UTF8);
                for (int i = 0; i < lines.Length; i++)
                {
                    string rawLine = lines[i];
                    string trimmed = rawLine.Trim();
                    if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith("#") || trimmed.StartsWith("//"))
                        continue;

                    string from = null;
                    string to = "";

                    if (rawLine.Contains("\t"))
                    {
                        string[] parts = rawLine.Split(new char[] { '\t' }, 2);
                        from = parts[0].Trim();
                        to = parts.Length > 1 ? parts[1].Trim() : "";
                    }
                    else if (rawLine.Contains("="))
                    {
                        string[] parts = rawLine.Split(new char[] { '=' }, 2);
                        from = parts[0].Trim();
                        to = parts.Length > 1 ? parts[1].Trim() : "";
                    }
                    else if (rawLine.Contains(","))
                    {
                        string[] parts = rawLine.Split(new char[] { ',' }, 2);
                        from = parts[0].Trim();
                        to = parts.Length > 1 ? parts[1].Trim() : "";
                    }

                    if (!string.IsNullOrEmpty(from))
                    {
                        customMappings.Add(new KeyValuePair<string, string>(from, to));
                    }
                }

                // 長い文字列を先に置換するため、Keyの長さ降順でソート（部分一致誤爆の防止）
                customMappings.Sort(delegate(KeyValuePair<string, string> a, KeyValuePair<string, string> b)
                {
                    return b.Key.Length.CompareTo(a.Key.Length);
                });

                loadedMappingFilePath = targetFile;
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine(string.Format("警告: 外部対応表の読み込み中にエラーが発生しました: {0}", ex.Message));
                Console.ResetColor();
            }
        }

        public static string CleanNameToWindows(string name)
        {
            if (string.IsNullOrEmpty(name)) return name;

            // Step 1: Unicode NFC正規化（濁点・半濁点の結合）
            string s = name.Normalize(NormalizationForm.FormC);

            // Step 1.5: 外部対応表の適用（最優先・1文字対複数文字も完全対応）
            if (customMappings != null && customMappings.Count > 0)
            {
                for (int m = 0; m < customMappings.Count; m++)
                {
                    s = s.Replace(customMappings[m].Key, customMappings[m].Value);
                }
            }

            // Step 2: 西欧ダイアクリティカルマーク除去 & 特殊ラテン文字展開 (pokémon -> pokemon, ß -> ss, æ -> ae, ø -> o 等 ※日本語の濁点・半濁点は完全保護)
            s = StripDiacritics(s);

            // Step 2.5: macOS / PDF由来の康熙部首 (U+2F00-U+2FD5) ・ CJK部首補助 (U+2E80-U+2EFF) を標準JIS漢字へ正規化救出 (⼀ -> 一 等)
            s = Regex.Replace(s, @"[\u2E80-\u2FD5]", delegate(Match m)
            {
                return m.Value.Normalize(NormalizationForm.FormKC);
            });

            // Step 3: 波ダッシュ・JIS/CP932マッピング不一致記号の是正
            s = s
                .Replace('\u301C', '\uFF5E') // 〜 (WAVE DASH) -> ～ (全角チルダ 0x8160)
                .Replace('\u3030', '\uFF5E') // 〰 (WAVY DASH) -> ～ (全角チルダ 0x8160)
                .Replace('\u2016', '\u2225') // ‖ -> ∥ (双柱 0x8161)
                .Replace('\u00A2', '\uFFE0') // ¢ -> ￠ (セント 0x8191)
                .Replace('\u00A3', '\uFFE1') // £ -> ￡ (ポンド 0x8192)
                .Replace('\u00AC', '\uFFE2'); // ¬ -> ￢ (ノット 0x81CA)

            // Step 4: 丸数字の置換・展開（黒丸数字->白丸数字 / 21以降は(21)等へ展開）
            s = NormalizeCircledNumbers(s);

            // Step 5: macOS特有の自動変換・全半角ダッシュ・ハイフン・地雷文字の完全標準化
            s = s
                // あらゆるUnicodeハイフン・ダッシュ・マイナス類 (全13種) -> 半角ハイフン '-' (0x2D)
                .Replace('\u2010', '-') // HYPHEN (‐)
                .Replace('\u2011', '-') // NON-BREAKING HYPHEN (‑) ※CP932非対応
                .Replace('\u2012', '-') // FIGURE DASH (‒) ※CP932非対応
                .Replace('\u2013', '-') // EN DASH (–) ※CP932非対応
                .Replace('\u2014', '-') // EM DASH (—) ※CP932非対応 (macOS全角ダッシュ)
                .Replace('\u2015', '-') // HORIZONTAL BAR (―)
                .Replace('\u2212', '-') // MINUS SIGN (−) ※CP932非対応 (macOS全角マイナス)
                .Replace('\u2043', '-') // HYPHEN BULLET (⁃) ※CP932非対応
                .Replace('\uFE58', '-') // SMALL EM DASH (﹘) ※CP932非対応
                .Replace('\uFE63', '-') // SMALL HYPHEN-MINUS (﹣) ※CP932非対応
                .Replace('\u2E3A', '-') // TWO-EM DASH (⸺) ※CP932非対応
                .Replace('\u2E3B', '-') // THREE-EM DASH (⸻) ※CP932非対応
                .Replace('\u30A0', '-') // KATAKANA-HIRAGANA DOUBLE HYPHEN (゠) ※CP932非対応
                                        // 特殊スペース -> 半角空白 ' '
                .Replace('\u00A0', ' ') // NO-BREAK SPACE
                .Replace('\u2002', ' ') // EN SPACE
                .Replace('\u2003', ' ') // EM SPACE
                .Replace('\u2009', ' ') // THIN SPACE
                .Replace('\u202F', ' ') // NARROW NO-BREAK SPACE
                                        // 不可視文字・ゴミ -> 削除
                .Replace("\u00AD", "")  // SOFT HYPHEN (不可視の改行ハイフン)
                .Replace("\u200B", "")  // ZERO WIDTH SPACE
                .Replace("\u200C", "")  // ZERO WIDTH NON-JOINER
                .Replace("\u200D", "")  // ZERO WIDTH JOINER
                .Replace("\uFEFF", "")  // BOM
                .Replace("\uFE0E", "")  // VARIATION SELECTOR-15
                .Replace("\uFE0F", "")  // VARIATION SELECTOR-16
                                        // カーリークォート -> シングルクォート
                .Replace('\u2018', '\'')
                .Replace('\u2019', '\'')
                .Replace('\u201A', '\'')
                .Replace('\u201B', '\'');

            // Step 6: かな・記号ファミリー集約（MangaMeeya・Win32救出：変換先はすべて純粋なCP932文字のみ）
            // CP932非対応のひらがな・カタカナ（ゔ, ゕ, ゖ, ヷ, ヸ, ヹ, ヺ）をCP932対応カナへ救出
            s = s
                .Replace("\u3094", "ヴ")   // ゔ -> ヴ (0x8394)
                .Replace("\u3095", "ヵ")   // ゕ -> ヵ (0x8395)
                .Replace("\u3096", "ヶ")   // ゖ -> ヶ (0x8396)
                .Replace("\u30F7", "ヴァ") // ヷ -> ヴァ
                .Replace("\u30F8", "ヴィ") // ヸ -> ヴィ
                .Replace("\u30F9", "ヴェ") // ヹ -> ヴェ
                .Replace("\u30FA", "ヴォ"); // ヺ -> ヴォ

            // ハート系絵文字 および Unicode黒ハート(\u2665) -> Shift_JIS外字黒ハート「\uE0A5」(0xF0E6)へ集約
            s = Regex.Replace(s, @"\u2665|\u2764\uFE0F?|[\uD83D][\uDC93-\uDC9F]|[\uD83E][\uDD70\uDE0D-\uDE0F\uDFE1]|[\uD83E][\uDE75-\uDE77]", "\uE0A5");

            // Unicode白ハート(\u2661) -> Shift_JIS外字白ハート「\uE006」(0xF046)へ変換
            s = s.Replace("\u2661", "\uE006");

            // 三角系絵文字・右左小三角 -> 「▲」「△」「▼」「▽」へ（Shift_JIS: 0x81A3, 0x81A2, 0x81A5, 0x81A4）
            s = Regex.Replace(s, @"[\uD83D]\uDD3A|[\u25B4\u25B6\u25B8\u25BA]", "▲"); // 🔺 ▴ ▶ ▸ ►
            s = Regex.Replace(s, @"[\u25B5\u25B7\u25B9\u25BB]", "△");                // ▵ ▷ ▹ ▻
            s = Regex.Replace(s, @"[\uD83D]\uDD3B|[\u25BE\u25C0\u25C2\u25C4]", "▼"); // 🔻 ▾ ◀ ◂ ◄
            s = Regex.Replace(s, @"[\u25BF\u25C1\u25C3\u25C5]", "▽");                // ▿ ◁ ◃ ◅

            // ダイヤ系絵文字 -> 「◆」「◇」へ（Shift_JIS: 0x819F, 0x819E）
            s = Regex.Replace(s, @"[\uD83D][\uDD36\uDD37]", "◆"); // 🔶 🔷
            s = Regex.Replace(s, @"[\uD83D][\uDD38\uDD39]", "◇"); // 🔸 🔹

            // 丸系絵文字 -> 「●」「○」へ（Shift_JIS: 0x819C, 0x819B）
            s = Regex.Replace(s, @"\u26AB|[\uD83D][\uDD34\uDD35]|[\uD83E][\uDFE0-\uDFE4]", "●"); // 🔴 🔵 ⚫ カラー丸
            s = Regex.Replace(s, @"\u26AA", "○"); // ⚪

            // 星系・音符系 -> 「★」「♪」へ（Shift_JIS: 0x819A, 0x81F4）
            s = Regex.Replace(s, @"\u2B50|\u2728|[\uD83C][\uDF1F\uDF20]|[\uD83D][\uDCAB]|\u2606", "★");
            s = Regex.Replace(s, @"[\uD83C][\uDFB5\uDFB6\uDFBC]|[\u2669\u266B\u266C]", "♪");

            // Step 7: Windows禁止文字・パス区切りの安全な全角化
            s = s
                .Replace('/', '／')
                .Replace('\\', '＼')
                .Replace(':', '：')
                .Replace('*', '＊')
                .Replace('?', '？')
                .Replace('"', '”')
                .Replace('<', '＜')
                .Replace('>', '＞')
                .Replace('|', '｜');

            // Step 8: サロゲートペア絵文字の単一「■」化 & Shift_JIS (CP932) 外文字の厳密な豆腐化
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < s.Length; i++)
            {
                // サロゲートペア（2文字組の絵文字等）は単一の「■」に集約して2文字分進める
                if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
                {
                    sb.Append('■');
                    i++;
                    continue;
                }

                char c = s[i];
                if (c < 32) continue; // 制御文字はスキップ

                // Shift_JIS (CP932) で表現可能か往復テスト（外字 0xF040〜0xF9FC を含む純粋なCP932文字のみ通過）
                byte[] bytes = ShiftJis.GetBytes(new char[] { c });
                char[] roundtrip = ShiftJis.GetChars(bytes);
                if (roundtrip.Length == 1 && roundtrip[0] == c)
                {
                    sb.Append(c);
                }
                else
                {
                    sb.Append('■');
                }
            }

            return sb.ToString().TrimEnd(new char[] { ' ', '.' });
        }

        static string StripDiacritics(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;

            // NFDで分解されない西欧特殊ラテン文字・合字をASCII英字へ展開
            string pre = text
                .Replace("\u00DF", "ss") // ß (ドイツ語エスツェット)
                .Replace("\u1E9E", "SS") // ẞ (大文字エスツェット)
                .Replace("\u00C6", "AE") // Æ
                .Replace("\u00E6", "ae") // æ
                .Replace("\u0152", "OE") // Œ
                .Replace("\u0153", "oe") // œ
                .Replace("\u00D8", "O")  // Ø (北欧ストローク付きO)
                .Replace("\u00F8", "o")  // ø
                .Replace("\u0141", "L")  // Ł (ポーランド語ストローク付きL)
                .Replace("\u0142", "l")  // ł
                .Replace("\u0110", "D")  // Đ (ストローク付きD)
                .Replace("\u0111", "d")  // đ
                .Replace("\u00D0", "D")  // Ð (アイスランド語エズ)
                .Replace("\u00F0", "d")  // ð
                .Replace("\u00DE", "TH") // Þ (アイスランド語ソーン)
                .Replace("\u00FE", "th"); // þ

            string normalized = pre.Normalize(NormalizationForm.FormD);
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < normalized.Length; i++)
            {
                char c = normalized[i];

                // ★日本語の結合濁点 (\u3099) と結合半濁点 (\u309A) は絶対に保護・保持する！
                if (c == '\u3099' || c == '\u309A')
                {
                    sb.Append(c);
                    continue;
                }

                // ラテン言語のダイアクリティカルマーク（é -> e 等）のみを除去
                UnicodeCategory uc = CharUnicodeInfo.GetUnicodeCategory(c);
                if (uc != UnicodeCategory.NonSpacingMark)
                {
                    sb.Append(c);
                }
            }
            return sb.ToString().Normalize(NormalizationForm.FormC);
        }

        static string NormalizeCircledNumbers(string text)
        {
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                int code = (int)c;

                // 黒丸数字 ❶〜❿ (\u2776 - \u277F) -> ①〜⑩ (\u2460 - \u2469)
                if (code >= 0x2776 && code <= 0x277F)
                {
                    sb.Append((char)(code - 0x2776 + 0x2460));
                }
                // 黒丸数字 ⓫〜⓴ (\u2780 - \u2789) -> ⑪〜⑳ (\u246A - \u2473)
                else if (code >= 0x2780 && code <= 0x2789)
                {
                    sb.Append((char)(code - 0x2780 + 0x246A));
                }
                // 白丸数字 ㉑〜㉟ (\u3251 - \u325F) -> (21)〜(35)
                else if (code >= 0x3251 && code <= 0x325F)
                {
                    sb.Append(string.Format("({0})", code - 0x3251 + 21));
                }
                // 白丸数字 ㊱〜㊿ (\u32B1 - \u32BF) -> (36)〜(50)
                else if (code >= 0x32B1 && code <= 0x32BF)
                {
                    sb.Append(string.Format("({0})", code - 0x32B1 + 36));
                }
                else
                {
                    sb.Append(c);
                }
            }
            return sb.ToString();
        }

        // ====================================================================
        // 対象探索と処理分岐
        // ====================================================================
        static void ProcessTarget(string target)
        {
            if (File.Exists(target))
            {
                NormalizeFile(target);
                return;
            }

            if (Directory.Exists(target))
            {
                ProcessDirectory(target);
                return;
            }

            if (target.Contains("*") || target.Contains("?"))
            {
                ProcessWildcard(target);
                return;
            }

            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine(string.Format("警告: 対象が見つかりません: {0}", target));
            Console.ResetColor();
        }

        static void ProcessWildcard(string pattern)
        {
            string dir = Path.GetDirectoryName(pattern);
            if (string.IsNullOrEmpty(dir)) dir = ".";
            string searchPattern = Path.GetFileName(pattern);

            if (!Directory.Exists(dir))
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine(string.Format("警告: ディレクトリが見つかりません: {0}", dir));
                Console.ResetColor();
                return;
            }

            SearchOption opt = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;

            string[] files = Directory.GetFiles(dir, searchPattern, opt);
            foreach (string file in files)
            {
                NormalizeFile(file);
            }

            string[] dirs = Directory.GetDirectories(dir, searchPattern, opt);
            Array.Sort(dirs, (a, b) => b.Length.CompareTo(a.Length));
            foreach (string d in dirs)
            {
                NormalizeDirectoryName(d);
            }
        }

        static void ProcessDirectory(string dirPath)
        {
            string[] files = Directory.GetFiles(dirPath);
            foreach (string file in files)
            {
                NormalizeFile(file);
            }

            string[] subDirs = Directory.GetDirectories(dirPath);
            foreach (string subDir in subDirs)
            {
                if (recursive)
                {
                    ProcessDirectory(subDir);
                }
                NormalizeDirectoryName(subDir);
            }
        }

        // ====================================================================
        // ファイル処理・衝突解決
        // ====================================================================
        static void NormalizeFile(string filePath)
        {
            string fileName = Path.GetFileName(filePath);
            string winName = CleanNameToWindows(fileName);

            if (fileName.Equals(winName, StringComparison.Ordinal))
            {
                return;
            }

            string dir = Path.GetDirectoryName(filePath);
            string targetPath = Path.Combine(dir, winName);

            if (File.Exists(targetPath))
            {
                HandleFileCollision(filePath, targetPath, fileName, winName);
                return;
            }

            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.Write("[FILE] ");
            Console.ResetColor();
            PrintDiff(fileName, winName);

            if (!dryRun)
            {
                try
                {
                    File.Move(filePath, targetPath);
                    filesRenamed++;
                }
                catch (Exception ex)
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine(string.Format("  └─ リネーム失敗: {0}", ex.Message));
                    Console.ResetColor();
                }
            }
            else
            {
                filesRenamed++;
            }
        }

        static void HandleFileCollision(string srcPath, string destPath, string srcName, string destName)
        {
            FileInfo srcInfo = new FileInfo(srcPath);
            FileInfo destInfo = new FileInfo(destPath);

            bool isSameSize = (srcInfo.Length == destInfo.Length);
            string srcHash = isSameSize ? ComputeMD5(srcPath) : "サイズ相違のため未計算";
            string destHash = isSameSize ? ComputeMD5(destPath) : "サイズ相違のため未計算";
            bool isIdentical = isSameSize && (srcHash == destHash);

            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("--------------------------------------------------");
            Console.WriteLine(string.Format("[衝突検知] 同名のファイルが既に存在します: \"{0}\"", destName));
            Console.ResetColor();
            Console.WriteLine(string.Format("  ├─ 既存(Win): {0:#,##0} bytes [MD5: {1}]", destInfo.Length, destHash));
            Console.WriteLine(string.Format("  ├─ 対象(Src): {0:#,##0} bytes [MD5: {1}]", srcInfo.Length, srcHash));
            Console.Write("  └─ 判定: ");

            if (isIdentical)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("【完全一致】バイナリが同一です（統合可能）");
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("【内容不一致】内容が異なる別ファイルです（上書き危険）");
            }
            Console.ResetColor();

            if (dryRun)
            {
                Console.WriteLine("  └─ [テスト実行] 変更は行いません。");
                filesSkipped++;
                return;
            }

            char action = 'Y';

            if (!forceMode && !applyAll)
            {
                Console.WriteLine("処理を選択してください:");
                Console.WriteLine("  [Y] 統合/連番 : 同一なら1つに統合(対象削除)、異なるならWindows準拠の連番で保存");
                Console.WriteLine("  [N] スキップ  : 何もせずスキップ（両方そのまま残す）");
                Console.WriteLine("  [R] 連番保存  : 内容に関わらず強制的にWindows準拠の連番で別名保存");
                Console.WriteLine("  [A] すべて自動: 以降、同一なら自動統合、異なるなら自動で連番保存");
                Console.Write("選択 (Y/N/R/A) [既定: Y]: ");

                string input = Console.ReadLine();
                if (!string.IsNullOrEmpty(input))
                {
                    action = char.ToUpperInvariant(input.Trim()[0]);
                }
            }

            if (action == 'A')
            {
                applyAll = true;
                action = 'Y';
            }

            if (action == 'N')
            {
                Console.WriteLine("  └─ [スキップ] 変更せずスキップしました。");
                filesSkipped++;
                return;
            }

            if (action == 'R' || (action == 'Y' && !isIdentical))
            {
                string numberedPath = GetNextAvailableNumberedPath(destPath);
                string numberedName = Path.GetFileName(numberedPath);

                try
                {
                    File.Move(srcPath, numberedPath);
                    Console.ForegroundColor = ConsoleColor.Cyan;
                    Console.WriteLine(string.Format("  └─ [連番保存] {0} として保存しました。", numberedName));
                    Console.ResetColor();
                    filesRenamed++;
                }
                catch (Exception ex)
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine(string.Format("  └─ 連番リネーム失敗: {0}", ex.Message));
                    Console.ResetColor();
                }
            }
            else if (action == 'Y' && isIdentical)
            {
                try
                {
                    File.Delete(srcPath);
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine("  └─ [統合完了] 重複した対象ファイルを削除し、1つにまとめました。");
                    Console.ResetColor();
                    filesMerged++;
                }
                catch (Exception ex)
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine(string.Format("  └─ 統合削除失敗: {0}", ex.Message));
                    Console.ResetColor();
                }
            }
        }

        static string GetNextAvailableNumberedPath(string targetPath)
        {
            if (!File.Exists(targetPath)) return targetPath;

            string dir = Path.GetDirectoryName(targetPath);
            string nameWithoutExt = Path.GetFileNameWithoutExtension(targetPath);
            string ext = Path.GetExtension(targetPath);

            int index = 1;
            string candidatePath;
            do
            {
                string candidateName = string.Format("{0} ({1}){2}", nameWithoutExt, index, ext);
                candidatePath = Path.Combine(dir, candidateName);
                index++;
            } while (File.Exists(candidatePath));

            return candidatePath;
        }

        static string ComputeMD5(string filePath)
        {
            try
            {
                using (FileStream fs = File.OpenRead(filePath))
                using (MD5 md5 = MD5.Create())
                {
                    byte[] hash = md5.ComputeHash(fs);
                    StringBuilder sb = new StringBuilder();
                    for (int i = 0; i < hash.Length; i++)
                    {
                        sb.Append(hash[i].ToString("x2"));
                    }
                    return sb.ToString();
                }
            }
            catch
            {
                return "ハッシュ計算エラー";
            }
        }

        // ====================================================================
        // フォルダー名処理
        // ====================================================================
        static void NormalizeDirectoryName(string dirPath)
        {
            string dirName = Path.GetFileName(dirPath);
            string winDirName = CleanNameToWindows(dirName);

            if (dirName.Equals(winDirName, StringComparison.Ordinal))
            {
                return;
            }

            string parentPath = Path.GetDirectoryName(dirPath);
            string targetDirPath = Path.Combine(parentPath, winDirName);

            Console.ForegroundColor = ConsoleColor.Magenta;
            Console.Write("[DIR]  ");
            Console.ResetColor();
            PrintDiff(dirName, winDirName);

            if (!dryRun)
            {
                try
                {
                    Directory.Move(dirPath, targetDirPath);
                    dirsRenamed++;
                }
                catch (Exception ex)
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine(string.Format("  └─ リネーム失敗: {0}", ex.Message));
                    Console.ResetColor();
                }
            }
            else
            {
                dirsRenamed++;
            }
        }

        // ====================================================================
        // 文字コード診断モード (-c / --check)
        // ====================================================================
        static void RunCheckDiagnostic(string input)
        {
            string sample = input;
            bool isPath = false;
            try
            {
                if (File.Exists(input) || Directory.Exists(input))
                {
                    string fileName = Path.GetFileName(input.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                    if (!string.IsNullOrEmpty(fileName))
                    {
                        sample = fileName;
                        isPath = true;
                    }
                }
            }
            catch
            {
                // パスとして無効な文字（?や*など）を含む文字列リテラルの場合はそのまま診断
            }

            Console.WriteLine("================================================================================");
            Console.WriteLine(string.Format("  {0} v{1} - 文字コード・Win32 (CP932/MangaMeeya) 互換性診断", AppName, AppVersion));
            Console.WriteLine("================================================================================");
            if (isPath)
            {
                Console.WriteLine(string.Format("対象パス  : {0}", input));
            }
            Console.WriteLine(string.Format("診断文字列: {0}", sample));
            Console.WriteLine("--------------------------------------------------------------------------------");
            Console.WriteLine("No. | 文字 | Unicode         | Shift-JIS   | Win32 (CP932) 判定    | 変換後");
            Console.WriteLine("----+------+-----------------+-------------+-----------------------+------------");

            int charIndex = 1;
            int ngCount = 0;
            string forbidden = "\\/:*?\"<>|";

            for (int i = 0; i < sample.Length; i++)
            {
                string unit;
                string unicodeHex;
                bool isSurrogate = false;

                if (char.IsHighSurrogate(sample[i]) && i + 1 < sample.Length && char.IsLowSurrogate(sample[i + 1]))
                {
                    int codePoint = char.ConvertToUtf32(sample[i], sample[i + 1]);
                    unit = new string(new char[] { sample[i], sample[i + 1] });
                    unicodeHex = string.Format("U+{0:X5} (SP)", codePoint);
                    isSurrogate = true;
                    i++;
                }
                else
                {
                    char c = sample[i];
                    unit = c.ToString();
                    unicodeHex = string.Format("U+{0:X4}", (int)c);
                }

                string sjisHex;
                string statusText;
                ConsoleColor statusColor;

                if (isSurrogate)
                {
                    sjisHex = "---- (3F:?)";
                    statusText = "NG (サロゲート絵文字)";
                    statusColor = ConsoleColor.Red;
                    ngCount++;
                }
                else
                {
                    char c = unit[0];
                    byte[] bytes = ShiftJis.GetBytes(new char[] { c });
                    char[] roundtrip = ShiftJis.GetChars(bytes);
                    bool validCp932 = (roundtrip.Length == 1 && roundtrip[0] == c);

                    StringBuilder hexSb = new StringBuilder();
                    for (int b = 0; b < bytes.Length; b++)
                    {
                        hexSb.AppendFormat("{0:X2}", bytes[b]);
                    }

                    if (!validCp932)
                    {
                        sjisHex = "---- (3F:?)";
                        statusText = "NG (CP932非対応)";
                        statusColor = ConsoleColor.Red;
                        ngCount++;
                    }
                    else if (forbidden.IndexOf(c) >= 0)
                    {
                        sjisHex = hexSb.ToString();
                        statusText = "NG (Win禁止文字)";
                        statusColor = ConsoleColor.Red;
                        ngCount++;
                    }
                    else if (c < 32)
                    {
                        sjisHex = hexSb.ToString();
                        statusText = "NG (制御文字)";
                        statusColor = ConsoleColor.Red;
                        ngCount++;
                    }
                    else if (c >= '\uE000' && c <= '\uE757')
                    {
                        sjisHex = hexSb.ToString();
                        statusText = "OK (CP932外字領域)";
                        statusColor = ConsoleColor.Cyan;
                    }
                    else
                    {
                        sjisHex = hexSb.ToString();
                        statusText = "OK (CP932標準)";
                        statusColor = ConsoleColor.Green;
                    }
                }

                string convertedUnit = CleanNameToWindows(unit);
                if (unit == " " || unit == ".")
                {
                    convertedUnit = unit; // 単体診断時は末尾トリムで消えないようそのまま表示
                }

                Console.Write(string.Format("{0,3} | {1,-4} | {2,-15} | {3,-11} | ", charIndex, unit, unicodeHex, sjisHex));
                Console.ForegroundColor = statusColor;
                Console.Write(string.Format("{0,-15}", statusText));
                Console.ResetColor();
                Console.Write(" | ");
                if (convertedUnit != unit)
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine(string.Format("{0} (変換)", convertedUnit));
                    Console.ResetColor();
                }
                else
                {
                    Console.WriteLine(convertedUnit);
                }

                charIndex++;
            }

            Console.WriteLine("--------------------------------------------------------------------------------");
            string sanitizedFull = CleanNameToWindows(sample);
            Console.Write("クレンジング結果: ");
            PrintDiff(sample, sanitizedFull);
            Console.WriteLine();

            if (ngCount == 0 && sample == sanitizedFull)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("総合判定: [OK] すべての文字が CP932 (Shift_JIS) に完全準拠しており、古のWin32アプリでそのまま読み込めます。");
            }
            else if (ngCount > 0)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine(string.Format("総合判定: [要変換] 古のWin32アプリで読み込めない文字が {0} 箇所含まれています（上記クレンジング結果への変換で解決します）。", ngCount));
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine("総合判定: [正規化推奨] CP932で読み込み可能ですが、クレンジング標準化の対象文字が含まれています。");
            }
            Console.ResetColor();
            Console.WriteLine();
        }

        // ====================================================================
        // ヘルプ・バージョン表示
        // ====================================================================
        static void ShowVersion()
        {
            Console.WriteLine(string.Format("{0} version {1}", AppName, AppVersion));
        }

        static void ShowHelp()
        {
            Console.WriteLine("==================================================");
            Console.WriteLine(string.Format("  {0} v{1} - Name to Windows (Pole to Win!)", AppName, AppVersion));
            Console.WriteLine("==================================================");
            Console.WriteLine("使用方法: name2win.exe [オプション] <対象ファイル/フォルダー/パターン...>");
            Console.WriteLine();
            Console.WriteLine("オプション:");
            Console.WriteLine("  -r, --recursive   フォルダーまたはワイルドカード指定時に再帰処理を行います。");
            Console.WriteLine("  -f, --force       同名衝突時に問い合わせず自動解決（同一＝統合 / 相違＝連番保存）。");
            Console.WriteLine("  -t, --test        テスト実行 (Dry-Run)。実際には変更せずに対象を表示します。");
            Console.WriteLine("  -c, --check       文字コード診断モード。指定した文字列やファイル名のUnicode/CP932対応状況を1文字ずつ表形式で診断します。");
            Console.WriteLine("  -v, --version     バージョン情報を表示します。");
            Console.WriteLine("  -h, --help        このヘルプを表示します。");
            Console.WriteLine();
            Console.WriteLine("クレンジング仕様:");
            Console.WriteLine("  1. Unicode NFC正規化 (濁点・半濁点の1文字結合)");
            Console.WriteLine("  2. 外部対応表の適用 (name2win.mapping.txt があれば最優先置換・中国語等にも対応)");
            Console.WriteLine("  3. ダイアクリティカルマーク除去 (pokémon -> pokemon 等 ※日本語の濁点は完全保護)");
            Console.WriteLine("  4. 波ダッシュ是正 (\\u301C -> \\uFF5E 全角チルダ)");
            Console.WriteLine("  5. 丸数字の標準化 (❶〜❿ -> ①〜⑩, ㉑〜㊿ -> (21)〜(50))");
            Console.WriteLine("  6. Mac地雷文字の除去 (ダッシュ類->半角ハイフン, 特殊空白->半角空白, 不可視ゴミ消去)");
            Console.WriteLine("  7. かな・記号ファミリー集約 (ゔ->ヴ, ヷ〜ヺ->ヴァ〜ヴォ, ♥->外字黒ハート[F0E6], ♡->外字白ハート[F046], 三角・▶◀->▲△▼▽, ダイヤ->◆◇, 丸->●○, 星->★, 音符・♩♫♬->♪)");
            Console.WriteLine("  8. Windows禁止文字・パス区切りの安全な全角化 (/ -> ／, \\ -> ＼, : -> ： 等)");
            Console.WriteLine("  9. Shift_JIS (CP932) 厳密検証 (CP932外字ハート[F0E6/F046]を含む純粋なCP932文字のみ通過、CP932外の文字・絵文字はすべて単一「■」化)");
            Console.WriteLine();
            Console.WriteLine("外部対応表 (name2win.mapping.txt):");
            Console.WriteLine("  exeと同じ場所または実行フォルダーに対応表ファイルを置くことで、自由な置換が可能です。");
            Console.WriteLine("  書式: 変換元[Tabまたは=または,]変換先 (例: 酱=ちゃん / 💛=\uE006 / 🍳=料理)");
            Console.WriteLine();
            Console.WriteLine("使用例:");
            Console.WriteLine("  name2win.exe \"sample.zip\"                   (単一ファイル)");
            Console.WriteLine("  name2win.exe -c \"テスト♬♥♡ゔ▶◀.zip\"         (文字列やファイルのCP932互換性を1文字ずつ診断)");
            Console.WriteLine("  name2win.exe -t -r \"C:\\Downloads\\Discord\"   (事前に衝突や変更をテスト確認)");
            Console.WriteLine("  name2win.exe -r \"C:\\Downloads\\Discord\"      (対話モードで確認しながら実行)");
            Console.WriteLine("  name2win.exe -f -r \"C:\\Downloads\\Discord\"   (全自動で安全に一括解決)");
        }
    }
}