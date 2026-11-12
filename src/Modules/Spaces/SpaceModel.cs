using TeamBeats.Localization;
using TeamBeats.Primitives;

namespace TeamBeats.Modules.Spaces;

public static class SpaceKinds
{
    public const string Project = "project";
    public const string Team = "team";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal) { Project, Team };
}

public static class SpaceVisibilities
{
    public const string Public = "public";
    public const string Private = "private";
    public const string Secret = "secret";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal) { Public, Private, Secret };
}

public static class SpaceRoles
{
    public const string Owner = "owner";
    public const string Member = "member";
    public const string Viewer = "viewer";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal) { Owner, Member, Viewer };
}

public static class SpaceStatuses
{
    public const string Active = "active";
    public const string Archived = "archived";
}

public static class SpaceClassifications
{
    public const string Internal = "internal";
    public const string Confidential = "confidential";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal) { Internal, Confidential };
}

/// <summary>A board column of a task list. Moving a task to a done column completes it (US2 scenario 2).</summary>
public sealed record TemplateColumn(string? Name, bool Done = false);

public sealed record TemplateList(string? Name, IReadOnlyList<TemplateColumn>? Columns);

/// <summary>A starter task, in the list and column at those positions of the template.</summary>
public sealed record TemplateTask(string? Title, int List = 0, int Column = 0);

/// <summary>What a template gives a new space (FR-012): task lists with their columns, and starter tasks.</summary>
public sealed record TemplateDefinition(IReadOnlyList<TemplateList>? Lists, IReadOnlyList<TemplateTask>? Tasks);

/// <summary>
/// The built-in templates and the rules for tenant templates (spec 029 FR-012). Built-in text comes from resource keys
/// in the tenant's default language when a space is created; after that it is the space's own text.
/// </summary>
public static class SpaceTemplates
{
    public const int MaxLists = 10;
    public const int MaxColumns = 12;
    public const int MaxTasks = 100;
    public const int MaxNameLength = 80;
    public const int MaxTitleLength = 200;

    public static readonly IReadOnlyList<string> BuiltInKeys = [SpaceKinds.Project, SpaceKinds.Team];

    /// <summary>The built-in template for a key, in a locale, or null for an unknown key.</summary>
    public static TemplateDefinition? BuiltIn(string key, ILocalizer text, string locale)
    {
        string T(string k) => text.Get($"spaces.template.{k}", locale);
        TemplateColumn[] Columns() => [new(T("column.todo")), new(T("column.doing")), new(T("column.done"), Done: true)];
        return key switch
        {
            SpaceKinds.Project => new TemplateDefinition(
                [new TemplateList(T("project.list"), Columns())],
                [new TemplateTask(T("project.task.goals")), new TemplateTask(T("project.task.milestones")), new TemplateTask(T("project.task.team"))]),
            SpaceKinds.Team => new TemplateDefinition(
                [new TemplateList(T("team.list"), Columns())],
                [new TemplateTask(T("team.task.recurring"))]),
            _ => null,
        };
    }

    /// <summary>Checks a tenant template's definition and returns it trimmed.</summary>
    public static TemplateDefinition Validate(TemplateDefinition? definition, ValidationErrors errors, string field = "definition")
    {
        var lists = definition?.Lists ?? [];
        if (lists.Count is 0 or > MaxLists)
        {
            errors.Add($"{field}.lists", "validation.spaces.template_lists");
            return new TemplateDefinition([], []);
        }
        var cleanLists = new List<TemplateList>();
        for (var i = 0; i < lists.Count; i++)
        {
            var name = lists[i].Name?.Trim() ?? "";
            if (name.Length is 0 or > MaxNameLength)
            {
                errors.Add($"{field}.lists[{i}].name", "validation.spaces.name");
            }
            var columns = lists[i].Columns ?? [];
            if (columns.Count is 0 or > MaxColumns)
            {
                errors.Add($"{field}.lists[{i}].columns", "validation.spaces.template_columns");
            }
            var cleanColumns = new List<TemplateColumn>();
            for (var c = 0; c < columns.Count; c++)
            {
                var column = columns[c].Name?.Trim() ?? "";
                if (column.Length is 0 or > MaxNameLength)
                {
                    errors.Add($"{field}.lists[{i}].columns[{c}].name", "validation.spaces.name");
                }
                cleanColumns.Add(new TemplateColumn(column, columns[c].Done));
            }
            cleanLists.Add(new TemplateList(name, cleanColumns));
        }
        var tasks = definition?.Tasks ?? [];
        if (tasks.Count > MaxTasks)
        {
            errors.Add($"{field}.tasks", "validation.spaces.template_tasks");
        }
        var cleanTasks = new List<TemplateTask>();
        for (var i = 0; i < Math.Min(tasks.Count, MaxTasks); i++)
        {
            var title = tasks[i].Title?.Trim() ?? "";
            if (title.Length is 0 or > MaxTitleLength)
            {
                errors.Add($"{field}.tasks[{i}].title", "validation.spaces.task_title");
            }
            if (tasks[i].List < 0 || tasks[i].List >= cleanLists.Count || tasks[i].Column < 0
                || tasks[i].Column >= (cleanLists[tasks[i].List].Columns?.Count ?? 0))
            {
                errors.Add($"{field}.tasks[{i}]", "validation.spaces.template_task_place");
            }
            cleanTasks.Add(new TemplateTask(title, tasks[i].List, tasks[i].Column));
        }
        return new TemplateDefinition(cleanLists, cleanTasks);
    }
}
