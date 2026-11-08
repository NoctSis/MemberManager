using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using MemberManager.Common;

namespace MemberManager.BLL
{
    /// <summary>
    /// 极简 .xlsx 生成器（零第三方依赖，不引入 NPOI 及其 8 个传递包）。
    ///
    /// .xlsx 本质是 zip 包里的几份 OOXML：
    ///   [Content_Types].xml      部件清单
    ///   _rels/.rels              指向 xl/workbook.xml
    ///   xl/workbook.xml          sheet 列表
    ///   xl/_rels/workbook.xml.rels  sheet + styles 关系
    ///   xl/styles.xml            数字格式 / 字体 / 填充 / 边框
    ///   xl/worksheets/sheetN.xml 每个 sheet 的行列数据
    ///
    /// 取舍（对单机报表足够，Excel / WPS 均可打开）：
    ///   · 文本一律用 inlineStr —— 省掉 sharedStrings.xml 与字符串去重；
    ///   · 样式只建 5 个（普通 / 表头 / 金额 / 金额加粗 / 百分比），够用不膨胀；
    ///   · 支持列宽、冻结首行、单元格备注（备注即合并成一整行的说明文字）。
    /// </summary>
    public class ExcelWriter
    {
        /// <summary>样式索引，与 styles.xml 的 cellXfs 顺序一一对应。</summary>
        public enum S : short
        {
            Plain = 0,     // 普通左对齐
            Header = 1,    // 表头：加粗 + 浅灰底 + 细边框 + 居中
            Money = 2,     // 金额：#,##0.00 + 细边框
            MoneyHead = 3, // 金额 + 加粗居中（合计行）
            Percent = 4,   // 百分比：#,##0.0%
            Count = 5,     // 计数：#,##0 + 细边框
        }

        /// <summary>员工业绩表按员工区分的列配色（浅色系，12 色循环）。AARRGGBB。</summary>
        internal static readonly uint[] FILL_PALETTE = {
            0xFFE3F2FD, 0xFFFDE9D0, 0xFFE6F4EA, 0xFFFCE4EF,
            0xFFEDEAFD, 0xFFF0F4D6, 0xFFD9F2EF, 0xFFFBE3D9,
            0xFFE8E0F7, 0xFFD6ECF5, 0xFFF3DFD6, 0xFFE0F0DC,
        };

        /// <summary>(基础样式, 填充色) → cellXfs 索引，Save 时构建。</summary>
        private Dictionary<long, int> _styleMap;
        private List<(short, int)> _combos;

        private class Cell
        {
            public int Row;
            public int Col;
            public bool IsNumber;
            public string Text;
            public decimal Num;
            public S Style;
            public int Fill = -1;   // >=0 时用调色板填充色（按员工着色）；-1 不填充
        }

        private class MergedCell
        {
            public int Row1;
            public int Col1;
            public int Row2;
            public int Col2;
        }

        private class SheetData
        {
            public string Name;
            public List<Cell> Cells = new List<Cell>();
            public Dictionary<int, double> Widths = new Dictionary<int, double>();
            public bool FreezeHeader = true;
            public List<MergedCell> Merges = new List<MergedCell>();
        }

        private readonly List<SheetData> _sheets = new List<SheetData>();

        /// <summary>新建一个工作表。name 不能含 : \ / ? * [ ]，最长 31 字符。</summary>
        public ExcelWriter Sheet(string name)
        {
            string n = name == null ? "" : name.Trim();
            if (n.Length == 0) throw new BizException("工作表名不能为空");
            if (n.Length > 31) n = n.Substring(0, 31);
            _sheets.Add(new SheetData { Name = n });
            return this;
        }

        private SheetData Cur
        {
            get
            {
                if (_sheets.Count == 0) throw new BizException("请先创建工作表");
                return _sheets[_sheets.Count - 1];
            }
        }

        /// <summary>第 row 行（从 1 起）、第 col 列（从 1 起）写文本。</summary>
        public ExcelWriter Text(int row, int col, string text, S style = S.Plain, int fill = -1)
        {
            Cur.Cells.Add(new Cell { Row = row, Col = col, IsNumber = false, Text = text ?? "", Style = style, Fill = fill });
            return this;
        }

        /// <summary>第 row 行、第 col 列写数值（按 style 显示格式）。</summary>
        public ExcelWriter Num(int row, int col, decimal value, S style = S.Plain, int fill = -1)
        {
            Cur.Cells.Add(new Cell { Row = row, Col = col, IsNumber = true, Num = value, Style = style, Fill = fill });
            return this;
        }

        /// <summary>整行都是文本、且相邻单元格相同文本时，自动合并成一格（用于表头、说明行）。</summary>
        public ExcelWriter Row(int row, string[] cells, S style = S.Plain)
        {
            for (int i = 0; i < cells.Length; i++)
                Text(row, i + 1, cells[i], style);
            return this;
        }

        /// <summary>设置某列显示宽度（Excel 字符宽，非像素）。</summary>
        public ExcelWriter Width(int col, double width)
        {
            Cur.Widths[col] = width;
            return this;
        }

        /// <summary>一列按固定宽度设置。</summary>
        public ExcelWriter Widths(params double[] ws)
        {
            for (int i = 0; i < ws.Length; i++) Cur.Widths[i + 1] = ws[i];
            return this;
        }

        /// <summary>当前工作表是否冻结首行（默认冻结，便于长表上下滚动时表头一直在）。</summary>
        public ExcelWriter FreezeHeaders(bool freeze)
        {
            Cur.FreezeHeader = freeze;
            return this;
        }

        /// <summary>
        /// 合并单元格（1-based，含首尾）。用法：
        ///   .Merge(1, 1, 1, 7)   // 把第 1 行 A~G 合并成一个标题格
        ///   .Merge(5, 1, 7, 1)   // 把第 5~7 行 A 列合并（同一天三笔）
        /// 合并只影响视觉，不会拷贝内容——被合并区域内只有左上角单元格的值会显示。
        /// </summary>
        public ExcelWriter Merge(int row1, int col1, int row2, int col2)
        {
            if (row1 < 1 || col1 < 1 || row2 < 1 || col2 < 1) throw new BizException("合并坐标必须 >= 1");
            if (row2 < row1) { int t = row1; row1 = row2; row2 = t; }
            if (col2 < col1) { int t = col1; col1 = col2; col2 = t; }
            Cur.Merges.Add(new MergedCell { Row1 = row1, Col1 = col1, Row2 = row2, Col2 = col2 });
            return this;
        }

        /// <summary>把当前工作簿写成 .xlsx 文件，返回字节数。</summary>
        public long Save(string path)
        {
            if (_sheets.Count == 0) throw new BizException("工作簿没有工作表");
            // 1) 收集用到的 (基础样式, 填充色) 组合，建立 cellXfs 索引表。
            //    无填充的单元格沿用基础 6 样式（索引 0..5）；有填充的按出现顺序追加到 6 之后。
            _styleMap = new Dictionary<long, int>();
            _combos = new List<(short, int)>();
            for (int i = 0; i < 6; i++) _styleMap[i * 1000 + (-1)] = i;
            foreach (var sh in _sheets)
                foreach (var c in sh.Cells)
                    if (c.Fill >= 0)
                    {
                        long key = (long)c.Style * 1000 + c.Fill;
                        if (!_styleMap.ContainsKey(key)) { _styleMap[key] = 6 + _combos.Count; _combos.Add(((short)c.Style, c.Fill)); }
                    }
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create, false))
            {
                AddZipPart(zip, "[Content_Types].xml", ContentTypes());
                AddZipPart(zip, "_rels/.rels", RootRels());
                AddZipPart(zip, "xl/workbook.xml", Workbook());
                AddZipPart(zip, "xl/_rels/workbook.xml.rels", WorkbookRels());
                AddZipPart(zip, "xl/styles.xml", Styles(_combos.Count));
                for (int i = 0; i < _sheets.Count; i++)
                    AddZipPart(zip, "xl/worksheets/sheet" + (i + 1) + ".xml", SheetXml(_sheets[i]));
            }
            // 不能在 zip 关闭前读 fs.Position —— 那时中央目录与 EOCD 还没写，
            // 拿到的字节数比真实文件大小小一截（导出提示的体积会偏小）。
            return new FileInfo(path).Length;
        }

        private static void AddZipPart(ZipArchive zip, string entryName, string content)
        {
            var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
            byte[] buf = Encoding.UTF8.GetBytes(content);
            using (var st = entry.Open())
            {
                st.Write(buf, 0, buf.Length);
            }
        }

        // ---------- 各部件 XML ----------

        private string ContentTypes()
        {
            var sb = new StringBuilder(4096);
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            sb.Append("<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">");
            sb.Append("<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>");
            sb.Append("<Default Extension=\"xml\" ContentType=\"application/xml\"/>");
            sb.Append("<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>");
            sb.Append("<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>");
            for (int i = 0; i < _sheets.Count; i++)
                sb.Append("<Override PartName=\"/xl/worksheets/sheet" + (i + 1) + ".xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>");
            sb.Append("</Types>");
            return sb.ToString();
        }

        private static string RootRels()
        {
            return "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                   "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                   "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/>" +
                   "</Relationships>";
        }

        private string Workbook()
        {
            var sb = new StringBuilder(1024);
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            sb.Append("<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">");
            sb.Append("<workbookPr defaultThemeVersion=\"166925\"/>");
            sb.Append("<bookViews><workbookView xWindow=\"0\" yWindow=\"0\" windowWidth=\"16000\" windowHeight=\"9000\"/></bookViews>");
            sb.Append("<sheets>");
            for (int i = 0; i < _sheets.Count; i++)
                sb.Append("<sheet name=\"" + X(_sheets[i].Name) + "\" sheetId=\"" + (i + 1) + "\" r:id=\"rId" + (i + 1) + "\"/>");
            sb.Append("</sheets>");
            sb.Append("</workbook>");
            return sb.ToString();
        }

        private string WorkbookRels()
        {
            var sb = new StringBuilder(1024);
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            sb.Append("<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">");
            for (int i = 0; i < _sheets.Count; i++)
                sb.Append("<Relationship Id=\"rId" + (i + 1) + "\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet" + (i + 1) + ".xml\"/>");
            int stylesId = _sheets.Count + 1;
            sb.Append("<Relationship Id=\"rId" + stylesId + "\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>");
            sb.Append("</Relationships>");
            return sb.ToString();
        }

        private string Styles(int coloredCount)
        {
            int fillCount = 3 + FILL_PALETTE.Length;
            var sb = new StringBuilder(2048);
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            sb.Append("<styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">");
            sb.Append("<numFmts count=\"2\">");
            sb.Append("<numFmt numFmtId=\"164\" formatCode=\"#,##0.00\"/>");
            sb.Append("<numFmt numFmtId=\"165\" formatCode=\"#,##0.0%\"/>");
            sb.Append("</numFmts>");
            sb.Append("<fonts count=\"2\">");
            sb.Append("<font><sz val=\"10\"/><name val=\"Calibri\"/></font>");
            sb.Append("<font><b/><sz val=\"10\"/><color rgb=\"FF1F1F1F\"/><name val=\"Calibri\"/></font>");
            sb.Append("</fonts>");
            sb.Append("<fills count=\"" + fillCount + "\">");
            sb.Append("<fill><patternFill patternType=\"none\"/></fill>");
            sb.Append("<fill><patternFill patternType=\"gray125\"/></fill>");
            sb.Append("<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FFEDEDED\"/><bgColor indexed=\"64\"/></patternFill></fill>");
            foreach (uint col in FILL_PALETTE)
                sb.Append("<fill><patternFill patternType=\"solid\"><fgColor rgb=\"" + col.ToString("X8") + "\"/></patternFill></fill>");
            sb.Append("</fills>");
            sb.Append("<borders count=\"2\">");
            sb.Append("<border><left/><right/><top/><bottom/><diagonal/></border>");
            sb.Append("<border><left style=\"thin\"><color rgb=\"FF808080\"/></left><right style=\"thin\"><color rgb=\"FF808080\"/></right><top style=\"thin\"><color rgb=\"FF808080\"/></top><bottom style=\"thin\"><color rgb=\"FF808080\"/></bottom><diagonal/></border>");
            sb.Append("</borders>");
            sb.Append("<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>");
            int xfCount = 6 + coloredCount;
            sb.Append("<cellXfs count=\"" + xfCount + "\">");
            for (int i = 0; i < 6; i++) sb.Append(XfXml((short)i, -1));
            if (_combos != null) foreach (var kv in _combos) sb.Append(XfXml(kv.Item1, kv.Item2));
            sb.Append("</cellXfs>");
            sb.Append("<cellStyles count=\"1\"><cellStyle name=\"Normal\" xfId=\"0\" builtinId=\"0\"/></cellStyles>");
            sb.Append("</styleSheet>");
            return sb.ToString();
        }

        /// <summary>
        /// 生成一个 cellXfs 条目。baseStyle 决定数字格式 / 字体 / 边框 / 对齐；
        /// fill&lt;0 时表头与金额加粗沿用灰底（fillId=2），其余无填充；fill&gt;=0 时用调色板填充（fillId=3+fill）。
        /// </summary>
        private static string XfXml(short baseStyle, int fill)
        {
            int numFmt, font, border; string align; bool applyNum = false, applyFont = false, applyFillBase = false;
            switch (baseStyle)
            {
                case 0: numFmt = 0; font = 0; border = 1; align = "vertical=\"center\""; break;
                case 1: numFmt = 0; font = 1; border = 1; align = "horizontal=\"center\" vertical=\"center\" wrapText=\"1\""; applyFont = true; applyFillBase = true; break;
                case 2: numFmt = 164; font = 0; border = 1; align = "horizontal=\"right\""; applyNum = true; break;
                case 3: numFmt = 164; font = 1; border = 1; align = "horizontal=\"center\""; applyNum = true; applyFont = true; applyFillBase = true; break;
                case 4: numFmt = 165; font = 0; border = 1; align = "horizontal=\"right\""; applyNum = true; break;
                case 5: numFmt = 3; font = 0; border = 1; align = "horizontal=\"center\""; applyNum = true; break;
                default: numFmt = 0; font = 0; border = 1; align = "vertical=\"center\""; break;
            }
            int fillId = (fill >= 0) ? (3 + fill) : (applyFillBase ? 2 : 0);
            var s = new StringBuilder("<xf numFmtId=\"" + numFmt + "\" fontId=\"" + font + "\" fillId=\"" + fillId + "\" borderId=\"" + border + "\" xfId=\"0\"");
            if (applyNum) s.Append(" applyNumberFormat=\"1\"");
            if (applyFont) s.Append(" applyFont=\"1\"");
            if (fill >= 0 || applyFillBase) s.Append(" applyFill=\"1\"");
            s.Append(" applyBorder=\"1\"");
            s.Append("><alignment " + align + "/></xf>");
            return s.ToString();
        }

        /// <summary>(基础样式, 填充色) → 最终 cellXfs 索引（Save 时构建的映射）。</summary>
        private int StyleIndex(short baseStyle, int fill)
        {
            long key = (long)baseStyle * 1000 + (fill < 0 ? -1 : fill);
            int idx;
            if (_styleMap != null && _styleMap.TryGetValue(key, out idx)) return idx;
            return (int)baseStyle;
        }

        private string SheetXml(SheetData s)
        {
            var rows = new Dictionary<int, List<Cell>>();
            int maxRow = 0, maxCol = 0;
            foreach (Cell c in s.Cells)
            {
                List<Cell> list;
                if (!rows.TryGetValue(c.Row, out list)) rows[c.Row] = list = new List<Cell>();
                list.Add(c);
                if (c.Row > maxRow) maxRow = c.Row;
                if (c.Col > maxCol) maxCol = c.Col;
            }
            if (maxRow == 0 || maxCol == 0) return EmptySheetXml();

            var sb = new StringBuilder(8192);
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            sb.Append("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">");
            sb.Append("<dimension ref=\"A1:" + ColName(maxCol) + maxRow + "\"/>");
            // sheetViews 是 OOXML 必需元素——缺失时部分 Excel 版本会忽略 mergeCells（合并单元格不生效）
            sb.Append("<sheetViews><sheetView workbookViewId=\"0\" showGridLines=\"0\">");
            if (s.FreezeHeader)
            {
                sb.Append("<pane ySplit=\"1\" topLeftCell=\"A2\" activePane=\"bottomLeft\" state=\"frozen\"/>");
                sb.Append("<selection pane=\"bottomLeft\" activeCell=\"A2\" sqref=\"A2\"/>");
            }
            else
            {
                sb.Append("<selection pane=\"topLeft\" activeCell=\"A1\" sqref=\"A1\"/>");
            }
            sb.Append("</sheetView></sheetViews>");
            sb.Append("<sheetFormatPr defaultRowHeight=\"15\" customHeight=\"0\"/>");
            if (s.Widths.Count > 0)
            {
                sb.Append("<cols>");
                var cols = new List<int>(s.Widths.Keys);
                cols.Sort();
                foreach (int col in cols)
                    sb.Append("<col min=\"" + col + "\" max=\"" + col + "\" width=\"" + s.Widths[col].ToString("0.##", CultureInfo.InvariantCulture) + "\" customWidth=\"1\"/>");
                sb.Append("</cols>");
            }
            sb.Append("<sheetData>");
            var rowIds = new List<int>(rows.Keys);
            rowIds.Sort();
            foreach (int r in rowIds)
            {
                sb.Append("<row r=\"" + r + "\" ht=\"20\" customHeight=\"1\">");
                foreach (Cell c in rows[r])
                {
                    sb.Append("<c r=\"" + ColName(c.Col) + c.Row + "\" s=\"" + StyleIndex((short)c.Style, c.Fill) + "\"");
                    if (!c.IsNumber) sb.Append(" t=\"inlineStr\"");
                    if (c.IsNumber)
                    {
                        sb.Append("><v>" + c.Num.ToString(CultureInfo.InvariantCulture) + "</v></c>");
                    }
                    else if (c.Text.Length > 0)
                    {
                        sb.Append("><is><t xml:space=\"preserve\">" + X(c.Text) + "</t></is></c>");
                    }
                    else
                    {
                        sb.Append("/>");
                    }
                }
                sb.Append("</row>");
            }
            sb.Append("</sheetData>");
            // mergeCells 必须紧跟 sheetData 之后、pageMargins 之前（OOXML 规定顺序）
            if (s.Merges.Count > 0)
            {
                sb.Append("<mergeCells count=\"");
                sb.Append(s.Merges.Count.ToString(CultureInfo.InvariantCulture));
                sb.Append("\">");
                foreach (MergedCell m in s.Merges)
                    sb.Append("<mergeCell ref=\"" + ColName(m.Col1) + m.Row1 + ":" + ColName(m.Col2) + m.Row2 + "\"/>");
                sb.Append("</mergeCells>");
            }
            sb.Append("<pageMargins left=\"0.5\" right=\"0.5\" top=\"0.5\" bottom=\"0.5\" header=\"0.2\" footer=\"0.2\"/>");
            sb.Append("</worksheet>");
            return sb.ToString();
        }

        private static string EmptySheetXml()
        {
            return "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                   "<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">" +
                   "<sheetData/></worksheet>";
        }

        /// <summary>列号转 Excel 列名（1→A，27→AA）。</summary>
        private static string ColName(int col)
        {
            if (col < 1) col = 1;
            var sb = new StringBuilder();
            while (col > 0)
            {
                int rem = (col - 1) % 26;
                sb.Insert(0, (char)('A' + rem));
                col = (col - rem) / 26;
            }
            return sb.ToString();
        }

        /// <summary>XML 转义。</summary>
        private static string X(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length);
            foreach (char ch in s)
            {
                switch (ch)
                {
                    case '&': sb.Append("&amp;"); break;
                    case '<': sb.Append("&lt;"); break;
                    case '>': sb.Append("&gt;"); break;
                    case '"': sb.Append("&quot;"); break;
                    case '\'': sb.Append("&apos;"); break;
                    case '\r': sb.Append(" "); break;
                    case '\n': sb.Append(" "); break;
                    case '\t': sb.Append(" "); break;
                    default: sb.Append(ch); break;
                }
            }
            return sb.ToString();
        }
    }
}
