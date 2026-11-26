using CadAI.Mcp.AutoCAD.Core;
using CadAI.Mcp.AutoCAD.Services;

namespace CadAI.Mcp.AutoCAD.Tools
{
    internal static class CadToolBootstrap
    {
        public static void RegisterAllTools(CadToolRegistry registry)
        {
            // 图纸与全局状态
            DocumentService.Register(registry);
            LayoutService.Register(registry);
            StatisticsService.Register(registry);

            // 组织与样式
            LayerService.Register(registry);
            TextStyleService.Register(registry);

            // 选择集与实体检索
            SelectionService.Register(registry);
            EntityService.Register(registry);

            // 绘制与标注
            GeometryService.Register(registry);
            DimensionService.Register(registry);

            // 文字与图块
            TextService.Register(registry);
            BlockService.Register(registry);

            // 视图与兜底交付
            ViewService.Register(registry);
            CommandService.Register(registry);

            // 多文件批处理（侧数据库，不打开文档，快速模式）
            BatchService.Register(registry);

            // 分析与整理（测量统计 / 图层隔离 / DXF / 插块）
            AnalysisService.Register(registry);

            // 整图结构化导出（跨宿主：全量图元 + 几何 → JSON）
            DrawingExportService.Register(registry);

            // 发布 / 对比 / 表格 / 外部参照（页面设置 / 批量PDF / DWG diff / Table 读写 / Xref 管理）
            PublishService.Register(registry);
            VirtualTableService.Register(registry);

            // Office 文档（Excel 读写/表格导出、PDF 文本抽取/图册合并拆分）
            OfficeDocumentService.Register(registry);

            // 技能包按需加载（SKILL.md + 分领域参考文档）
            SkillService.Register(registry);
        }
    }
}
