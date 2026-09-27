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
        const string AppVersion = "1.4.1";

        static bool recursive = false;
        static bool dryRun = false;
        static bool forceMode = false;
        static bool applyAll = false;

        static int filesRenamed = 0;
        static int filesMerged = 0;
        static int filesSkipped = 0;
        static int dirsRenamed = 0;

        // Shift_JIS (CP932)
        static readonly Encoding ShiftJis = Encoding.GetEncoding(932);

        static void Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;

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
                Console.WriteLine("エラー: 対象のファイル、フォルダー、またはパターンが指定されていません。");
                Console.ResetColor();
                ShowHelp();
                return;
            }

            Console.WriteLine("==================================================");
            Console.WriteLine(string.Format("  {0} v{1} - Name to Windows (Pole to Win!)", AppName, AppVersion));
            Console.WriteLine("==================================================");
            Console.WriteLine(string.Format("再帰処理 (-r)  : {0}", recursive ? "有効" : "無効"));
            Console.WriteLine(string.Format("強制自動 (-f)  : {0}", forceMode ? "有効 (対話なし・自動解決)" : "無効 (衝突時に対話確認)"));
            Console.WriteLine(string.Format("テスト実行 (-t): {0}", dryRun ? "有効 (変更は適用されません)" : "無効 (実行)"));
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
        public static string CleanNameToWindows(string name)
        {
            if (string.IsNullOrEmpty(name)) return name;

            // Step 1: Unicode NFC正規化（濁点・半濁点の結合）
            string s = name.Normalize(NormalizationForm.FormC);

            // Step 2: ダイアクリティカルマークの除去（pokémon -> pokemon 等、濁点・半濁点は完全保護）
            s = StripDiacritics(s);

            // Step 3: 波ダッシュ問題是正（\u301C -> \uFF5E 全角チルダへ）
            s = s.Replace('\u301C', '\uFF5E');

            // Step 4: 丸数字の置換・展開（黒丸数字->白丸数字 / 21以降は(21)等へ展開）
            s = NormalizeCircledNumbers(s);

            // Step 5: macOS特有の自動変換・地雷文字の標準化
            s = s
                // ダッシュ・ハイフン類 -> 半角ハイフン '-'
                .Replace('\u2013', '-') // EN DASH
                .Replace('\u2014', '-') // EM DASH
                .Replace('\u2015', '-') // HORIZONTAL BAR
                .Replace('\u2212', '-') // MINUS SIGN
                                        // 特殊スペース -> 半角空白 ' '
                .Replace('\u00A0', ' ') // NO-BREAK SPACE
                .Replace('\u2002', ' ') // EN SPACE
                .Replace('\u2003', ' ') // EM SPACE
                .Replace('\u2009', ' ') // THIN SPACE
                .Replace('\u202F', ' ') // NARROW NO-BREAK SPACE
                                        // 不可視文字・ゴミ -> 削除
                .Replace("\u200B", "")  // ZERO WIDTH SPACE
                .Replace("\u200C", "")  // ZERO WIDTH NON-JOINER
                .Replace("\u200D", "")  // ZERO WIDTH JOINER
                .Replace("\uFEFF", "")  // BOM
                                        // カーリークォート -> シングルクォート
                .Replace('\u2018', '\'')
                .Replace('\u2019', '\'')
                .Replace('\u201A', '\'')
                .Replace('\u201B', '\'');

            // Step 6: 記号ファミリー集約（雰囲気救出）
            s = Regex.Replace(s, @"\u2764\uFE0F?|[\uD83D][\uDC93-\uDC9F]|[\uD83E][\uDE0D-\uDE0F]|\u2661", "♥");
            s = Regex.Replace(s, @"\u2B50|\u2728|[\uD83C][\uDF1F\uDF20]|[\uD83D][\uDCAB]|\u2606", "★");
            s = Regex.Replace(s, @"[\uD83C][\uDFB5\uDFB6\uDFBC]|\u266B", "♪");

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

            // Step 8: サロゲートペア絵文字の単一「■」化 & Shift_JIS外文字の豆腐化
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

                // Shift_JISで表現可能か往復テスト
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
            string normalized = text.Normalize(NormalizationForm.FormD);
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
            Console.WriteLine(string.Format("{0} -> {1}", fileName, winName));

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
            Console.WriteLine(string.Format("{0} -> {1}", dirName, winDirName));

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
            Console.WriteLine("  -v, --version     バージョン情報を表示します。");
            Console.WriteLine("  -h, --help        このヘルプを表示します。");
            Console.WriteLine();
            Console.WriteLine("クレンジング仕様:");
            Console.WriteLine("  1. Unicode NFC正規化 (濁点・半濁点の1文字結合)");
            Console.WriteLine("  2. ダイアクリティカルマーク除去 (pokémon -> pokemon 等 ※日本語の濁点は完全保護)");
            Console.WriteLine("  3. 波ダッシュ是正 (\\u301C -> \\uFF5E 全角チルダ)");
            Console.WriteLine("  4. 丸数字の標準化 (❶〜❿ -> ①〜⑩, ㉑〜㊿ -> (21)〜(50))");
            Console.WriteLine("  5. Mac地雷文字の除去 (ダッシュ類->半角ハイフン, 特殊空白->半角空白, 不可視ゴミ消去)");
            Console.WriteLine("  6. 記号ファミリー集約 (ハート系->♥, 星系->★, 音符系->♪)");
            Console.WriteLine("  7. Windows禁止文字・パス区切りの安全な全角化 (/ -> ／, \\ -> ＼, : -> ： 等)");
            Console.WriteLine("  8. Shift_JISフォールバック (サロゲートペア絵文字・未対応文字を単一「■」化)");
            Console.WriteLine();
            Console.WriteLine("使用例:");
            Console.WriteLine("  name2win.exe \"sample.zip\"                   (単一ファイル)");
            Console.WriteLine("  name2win.exe -t -r \"C:\\Downloads\\Discord\"   (事前に衝突や変更をテスト確認)");
            Console.WriteLine("  name2win.exe -r \"C:\\Downloads\\Discord\"      (対話モードで確認しながら実行)");
            Console.WriteLine("  name2win.exe -f -r \"C:\\Downloads\\Discord\"   (全自動で安全に一括解決)");
        }
    }
}