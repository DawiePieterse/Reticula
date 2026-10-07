using System.Text;

namespace Reticula.Infrastructure.Costing;

/// <summary>A small RFC 4180 reader: commas, double quotes, doubled quotes inside quotes, CRLF or LF.</summary>
public static class Csv
{
    public static List<List<string>> Read(string text)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else cell.Append(c);
                continue;
            }
            switch (c)
            {
                case '"': quoted = true; break;
                case ',': row.Add(cell.ToString()); cell.Clear(); break;
                case '\r': break;
                case '\n':
                    row.Add(cell.ToString()); cell.Clear();
                    if (row.Count > 1 || row[0].Length > 0) rows.Add(row);
                    row = [];
                    break;
                default: cell.Append(c); break;
            }
        }
        row.Add(cell.ToString());
        if (row.Count > 1 || row[0].Length > 0) rows.Add(row);
        return rows;
    }
}
