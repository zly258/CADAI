using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;

namespace CadAI.Mcp.AutoCAD.Core
{
    /// <summary>一个布局空间（模型空间或某张图纸空间布局）及其块表记录。</summary>
    internal sealed class CadSpace
    {
        public string Name;
        public bool IsModel;
        public int TabOrder;
        public ObjectId LayoutId;
        public BlockTableRecord Record;
    }

    /// <summary>
    /// 布局空间枚举。模型空间与全部图纸空间布局都按「布局字典」口径取，
    /// 保证名称与 AutoCAD 底部标签页一致（模型空间即名为 "Model" 的那一项）。
    /// </summary>
    internal static class CadSpaces
    {
        /// <summary>枚举图纸中的全部空间。includeModel / includePaper 可分别关闭。</summary>
        public static List<CadSpace> Enumerate(Database db, Transaction tr,
                                               bool includeModel = true, bool includePaper = true)
        {
            var result = new List<CadSpace>();
            if (db == null || tr == null) return result;

            var layoutDict = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
            foreach (DBDictionaryEntry entry in layoutDict)
            {
                var layout = tr.GetObject(entry.Value, OpenMode.ForRead) as Layout;
                if (layout == null) continue;
                if (layout.ModelType && !includeModel) continue;
                if (!layout.ModelType && !includePaper) continue;

                var btr = tr.GetObject(layout.BlockTableRecordId, OpenMode.ForRead) as BlockTableRecord;
                if (btr == null) continue;

                result.Add(new CadSpace
                {
                    Name = layout.LayoutName,
                    IsModel = layout.ModelType,
                    TabOrder = layout.TabOrder,
                    LayoutId = entry.Value,
                    Record = btr
                });
            }

            result.Sort((a, b) => a.TabOrder.CompareTo(b.TabOrder));
            return result;
        }

        /// <summary>只枚举模型空间。</summary>
        public static BlockTableRecord ModelSpace(Database db, Transaction tr)
        {
            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            return (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
        }

        /// <summary>按布局名（忽略大小写）找空间；找不到返回 null。</summary>
        public static CadSpace Find(Database db, Transaction tr, string layoutName)
        {
            if (string.IsNullOrWhiteSpace(layoutName)) return null;
            foreach (var space in Enumerate(db, tr))
            {
                if (string.Equals(space.Name, layoutName.Trim(), StringComparison.OrdinalIgnoreCase))
                    return space;
            }
            return null;
        }

        /// <summary>当前活动布局名（取不到时按模型空间返回）。</summary>
        public static string CurrentLayoutName(Document doc)
        {
            try
            {
                return LayoutManager.Current.CurrentLayout;
            }
            catch
            {
                try { return doc.Database.TileMode ? "Model" : "Layout1"; }
                catch { return "Model"; }
            }
        }

        /// <summary>跨空间遍历所有实体，回调 (空间, 实体)。只读打开。</summary>
        public static void ForEachEntity(Database db, Transaction tr, Action<CadSpace, Entity> visitor,
                                         bool includeModel = true, bool includePaper = true)
        {
            foreach (var space in Enumerate(db, tr, includeModel, includePaper))
            {
                foreach (ObjectId id in space.Record)
                {
                    Entity ent;
                    try { ent = tr.GetObject(id, OpenMode.ForRead) as Entity; }
                    catch { continue; }
                    if (ent == null) continue;
                    visitor(space, ent);
                }
            }
        }
    }
}
