using System;
using System.Collections;
using System.Collections.Generic;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using CadAI.Mcp.AutoCAD.Core;

namespace CadAI.Mcp.AutoCAD.Services
{
    internal static class BlockService
    {
        public static void Register(CadToolRegistry registry)
        {
            registry.Register("cad_block_list", HandleList,
                CadSchema.Function("cad_block_list",
                    "List all block definitions in the current drawing (including dynamic and attribute blocks, excluding layouts/xrefs).",
                    CadSchema.Obj()));

            registry.Register("cad_block_get_attributes", HandleGetAttributes,
                CadSchema.Function("cad_block_get_attributes",
                    "Read all attribute tags and values from a BlockReference by its hex Handle (useful for title blocks and BOM tables).",
                    CadSchema.Obj(
                        CadSchema.Str("handle", "BlockReference hex Handle.", true))));

            registry.Register("cad_block_set_attributes", HandleSetAttributes,
                CadSchema.Function("cad_block_set_attributes",
                    "Update one or more attribute values on a BlockReference by Handle (e.g. title block fields like DESCRIPTION, DRAWING_NO).",
                    CadSchema.Obj(
                        CadSchema.Str("handle", "BlockReference hex Handle.", true),
                        CadSchema.Arr("attributes", "Key-value dictionary or list of {tag, value}.", "object", true))));
        }

        private static McpExecutionResult HandleList(string argsJson, Document doc, Transaction tr)
        {
            var db = doc.Database;
            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            var list = new List<object>();

            foreach (ObjectId id in bt)
            {
                var btr = (BlockTableRecord)tr.GetObject(id, OpenMode.ForRead);
                if (btr.IsLayout || btr.IsAnonymous || btr.IsFromExternalReference) continue;

                list.Add(new Dictionary<string, object>
                {
                    ["name"] = btr.Name,
                    ["hasAttributes"] = btr.HasAttributeDefinitions,
                    ["isDynamic"] = btr.IsDynamicBlock
                });
            }

            var data = new Dictionary<string, object>
            {
                ["ok"] = true,
                ["total"] = list.Count,
                ["blocks"] = list
            };

            return new McpExecutionResult { ToolName = "cad_block_list", Json = CadJson.Serialize(data), MutatesDrawing = false };
        }

        private static McpExecutionResult HandleGetAttributes(string argsJson, Document doc, Transaction tr)
        {
            var args = CadJson.Deserialize(argsJson);
            string hexHandle = CadJson.GetString(args, "handle");

            if (!long.TryParse(hexHandle, System.Globalization.NumberStyles.HexNumber, null, out long hVal))
                return new McpExecutionResult { ToolName = "cad_block_get_attributes", Json = "{\"ok\":false,\"error\":\"Invalid hex handle.\"}" };

            var db = doc.Database;
            if (!db.TryGetObjectId(new Handle(hVal), out ObjectId id) || id.IsNull)
                return new McpExecutionResult { ToolName = "cad_block_get_attributes", Json = "{\"ok\":false,\"error\":\"Block not found.\"}" };

            var br = tr.GetObject(id, OpenMode.ForRead) as BlockReference;
            if (br == null)
                return new McpExecutionResult { ToolName = "cad_block_get_attributes", Json = "{\"ok\":false,\"error\":\"Target is not a BlockReference.\"}" };

            var attrs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (ObjectId attId in br.AttributeCollection)
            {
                var att = tr.GetObject(attId, OpenMode.ForRead) as AttributeReference;
                if (att != null) attrs[att.Tag] = att.TextString;
            }

            var data = new Dictionary<string, object>
            {
                ["ok"] = true,
                ["handle"] = hexHandle,
                ["blockName"] = br.Name,
                ["attributes"] = attrs
            };

            return new McpExecutionResult { ToolName = "cad_block_get_attributes", Json = CadJson.Serialize(data), MutatesDrawing = false };
        }

        private static McpExecutionResult HandleSetAttributes(string argsJson, Document doc, Transaction tr)
        {
            var args = CadJson.Deserialize(argsJson);
            string hexHandle = CadJson.GetString(args, "handle");

            if (!long.TryParse(hexHandle, System.Globalization.NumberStyles.HexNumber, null, out long hVal))
                return new McpExecutionResult { ToolName = "cad_block_set_attributes", Json = "{\"ok\":false,\"error\":\"Invalid hex handle.\"}" };

            var db = doc.Database;
            if (!db.TryGetObjectId(new Handle(hVal), out ObjectId id) || id.IsNull)
                return new McpExecutionResult { ToolName = "cad_block_set_attributes", Json = "{\"ok\":false,\"error\":\"Block not found.\"}" };

            var br = tr.GetObject(id, OpenMode.ForRead) as BlockReference;
            if (br == null)
                return new McpExecutionResult { ToolName = "cad_block_set_attributes", Json = "{\"ok\":false,\"error\":\"Target is not a BlockReference.\"}" };

            // schema 声明的是 "Key-value dictionary or list of {tag, value}"，
            // 旧实现只认字典：模型按 schema 传数组时 updatedCount=0 却仍返回 ok=true，
            // AI 以为改成功了、图上什么都没变。两种形态都必须解析。
            var updates = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            if (args.TryGetValue("attributes", out var raw))
            {
                if (raw is Dictionary<string, object> dict)
                {
                    foreach (KeyValuePair<string, object> pair in dict)
                        updates[pair.Key] = pair.Value;
                }
                else if (raw is IEnumerable sequence && !(raw is string))
                {
                    foreach (object item in sequence)
                    {
                        if (!(item is Dictionary<string, object> entry)) continue;
                        string tag = CadJson.GetString(entry, "tag");
                        if (string.IsNullOrWhiteSpace(tag)) tag = CadJson.GetString(entry, "name");
                        if (string.IsNullOrWhiteSpace(tag)) continue;
                        if (entry.TryGetValue("value", out object value))
                            updates[tag] = value;
                    }
                }
            }

            if (updates.Count == 0)
            {
                var empty = new Dictionary<string, object>
                {
                    ["ok"] = false,
                    ["error"] = "attributes is empty or has no usable tag/value pairs.",
                    ["error_type"] = "bad_arguments",
                    ["handle"] = hexHandle,
                    ["updatedCount"] = 0
                };
                return new McpExecutionResult { ToolName = "cad_block_set_attributes", Json = CadJson.Serialize(empty), MutatesDrawing = false };
            }

            int updated = 0;
            foreach (ObjectId attId in br.AttributeCollection)
            {
                var att = tr.GetObject(attId, OpenMode.ForWrite) as AttributeReference;
                if (att != null && updates.TryGetValue(att.Tag, out var newVal))
                {
                    att.TextString = Convert.ToString(newVal);
                    updated++;
                }
            }

            // 0 命中不能伪装成功：标签名写错/块没有该属性时，必须让调用方看见。
            if (updated == 0)
            {
                var missed = new Dictionary<string, object>
                {
                    ["ok"] = false,
                    ["error"] = "No attribute tag matched the requested names.",
                    ["error_type"] = "attribute_not_found",
                    ["handle"] = hexHandle,
                    ["requested"] = new List<string>(updates.Keys),
                    ["updatedCount"] = 0
                };
                return new McpExecutionResult { ToolName = "cad_block_set_attributes", Json = CadJson.Serialize(missed), MutatesDrawing = false };
            }

            var data = new Dictionary<string, object>
            {
                ["ok"] = true,
                ["handle"] = hexHandle,
                ["updatedCount"] = updated
            };

            return new McpExecutionResult { ToolName = "cad_block_set_attributes", Json = CadJson.Serialize(data), MutatesDrawing = true };
        }
    }
}
