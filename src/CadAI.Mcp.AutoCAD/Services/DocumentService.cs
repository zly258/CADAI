using System;
using System.Collections.Generic;
using System.IO;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using CadAI.Mcp.AutoCAD.Core;

namespace CadAI.Mcp.AutoCAD.Services
{
    internal static class DocumentService
    {
        public static void Register(CadToolRegistry registry)
        {
            registry.Register("cad_doc_active_info", HandleActiveInfo,
                CadSchema.Function("cad_doc_active_info",
                    "Get metadata for the currently active drawing in AutoCAD: file name, path, read-only status, extents, and current space.",
                    CadSchema.Obj()));

            registry.Register("cad_doc_list", HandleList,
                CadSchema.Function("cad_doc_list",
                    "List all currently open drawings in the AutoCAD session.",
                    CadSchema.Obj()));

            registry.Register("cad_doc_save", HandleSave,
                CadSchema.Function("cad_doc_save",
                    "Save the active drawing, optionally saving as a new file path.",
                    CadSchema.Obj(
                        CadSchema.Str("file_path", "Optional new file path to save as. If omitted, saves to the current file."))));
        }

        private static McpExecutionResult HandleActiveInfo(string argsJson, Document doc, Transaction tr)
        {
            var db = doc.Database;
            var extMin = db.Extmin;
            var extMax = db.Extmax;

            var data = new Dictionary<string, object>
            {
                ["ok"] = true,
                ["fileName"] = Path.GetFileName(doc.Name),
                ["filePath"] = doc.Name,
                ["isReadOnly"] = doc.IsReadOnly,
                ["currentSpace"] = db.TileMode ? "ModelSpace" : "PaperSpace",
                ["extents"] = new Dictionary<string, object>
                {
                    ["min"] = new[] { Math.Round(extMin.X, 3), Math.Round(extMin.Y, 3), Math.Round(extMin.Z, 3) },
                    ["max"] = new[] { Math.Round(extMax.X, 3), Math.Round(extMax.Y, 3), Math.Round(extMax.Z, 3) }
                }
            };

            return new McpExecutionResult
            {
                ToolName = "cad_doc_active_info",
                Json = CadJson.Serialize(data),
                MutatesDrawing = false
            };
        }

        private static McpExecutionResult HandleList(string argsJson, Document doc, Transaction tr)
        {
            var list = new List<object>();
            foreach (Document d in Application.DocumentManager)
            {
                list.Add(new Dictionary<string, object>
                {
                    ["fileName"] = Path.GetFileName(d.Name),
                    ["filePath"] = d.Name,
                    ["isActive"] = d == doc,
                    ["isReadOnly"] = d.IsReadOnly
                });
            }

            var data = new Dictionary<string, object>
            {
                ["ok"] = true,
                ["total"] = list.Count,
                ["documents"] = list
            };

            return new McpExecutionResult
            {
                ToolName = "cad_doc_list",
                Json = CadJson.Serialize(data),
                MutatesDrawing = false
            };
        }

        private static McpExecutionResult HandleSave(string argsJson, Document doc, Transaction tr)
        {
            var args = CadJson.Deserialize(argsJson);
            string targetPath = CadJson.GetString(args, "file_path");

            try
            {
                if (string.IsNullOrWhiteSpace(targetPath))
                {
                    doc.Database.SaveAs(doc.Name, true, DwgVersion.Current, doc.Database.SecurityParameters);
                }
                else
                {
                    string validationError = ValidateSavePath(ref targetPath);
                    if (validationError != null)
                        return SaveFailure(validationError, "invalid_path");

                    doc.Database.SaveAs(targetPath, true, DwgVersion.Current, doc.Database.SecurityParameters);
                }

                return new McpExecutionResult
                {
                    ToolName = "cad_doc_save",
                    Json = "{\"ok\":true,\"message\":\"图纸保存成功。\"}",
                    MutatesDrawing = false
                };
            }
            catch (Exception ex)
            {
                return SaveFailure(ex.Message, "save_failed");
            }
        }

        /// <summary>
        /// SaveAs 的落盘路径校验。模型可以传任意字符串，若不校验，
        /// `..\..\`、盘符根、系统目录都会原样交给 AutoCAD。
        /// 返回 null 表示通过；通过时 targetPath 已归一化并补齐 .dwg。
        /// </summary>
        private static string ValidateSavePath(ref string targetPath)
        {
            try
            {
                targetPath = Path.GetFullPath(targetPath);

                string extension = Path.GetExtension(targetPath);
                if (extension.Length == 0)
                {
                    targetPath += ".dwg";
                }
                else if (!string.Equals(extension, ".dwg", StringComparison.OrdinalIgnoreCase))
                {
                    return "cad_doc_save only writes .dwg files; got '" + extension + "'.";
                }

                string directory = Path.GetDirectoryName(targetPath);
                if (string.IsNullOrWhiteSpace(directory))
                    return "Target path has no directory: " + targetPath;
                if (!Directory.Exists(directory))
                    return "Target directory does not exist: " + directory;

                string root = Path.GetPathRoot(directory);
                if (!string.IsNullOrEmpty(root)
                    && string.Equals(
                        directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                        root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                        StringComparison.OrdinalIgnoreCase))
                {
                    return "Refusing to save directly at a drive root: " + directory;
                }

                if (IsSystemDirectory(directory))
                    return "Refusing to save into a system directory: " + directory;

                return null;
            }
            catch (Exception ex)
            {
                return "Invalid target path: " + ex.Message;
            }
        }

        private static bool IsSystemDirectory(string directory)
        {
            string full = Path.GetFullPath(directory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;

            return IsUnder(full, Environment.GetFolderPath(Environment.SpecialFolder.Windows))
                || IsUnder(full, Environment.GetFolderPath(Environment.SpecialFolder.System))
                || IsUnder(full, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles))
                || IsUnder(full, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86));
        }

        private static bool IsUnder(string directoryWithSeparator, string root)
        {
            if (string.IsNullOrWhiteSpace(root)) return false;
            string normalized = Path.GetFullPath(root)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            return directoryWithSeparator.StartsWith(normalized, StringComparison.OrdinalIgnoreCase);
        }

        private static McpExecutionResult SaveFailure(string message, string errorType)
        {
            var data = new Dictionary<string, object>
            {
                ["ok"] = false,
                ["error"] = message,
                ["error_type"] = errorType
            };
            return new McpExecutionResult
            {
                ToolName = "cad_doc_save",
                Json = CadJson.Serialize(data),
                MutatesDrawing = false
            };
        }
    }
}
