using CommunityToolkit.Mvvm.ComponentModel;

namespace PicForLater.App.Models;

public sealed partial class CategoryOption : ObservableObject
{
    public CategoryOption(Guid id, string name, bool isAssigned)
    {
        Id = id;
        Name = name;
        IsAssigned = isAssigned;
    }

    public Guid Id { get; }

    public string Name { get; }

    public string AutomationId => $"CategoryAssignment_{Id:N}";

    [ObservableProperty]
    public partial bool IsAssigned { get; set; }
}

public sealed record CategoryFilterOption(Guid? Id, string Name);
