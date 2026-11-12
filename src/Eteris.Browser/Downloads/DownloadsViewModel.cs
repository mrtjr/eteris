using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Eteris.Browser.Downloads;

public partial class DownloadEntry(int id, string fullPath, string fileName, string? source = null) : ObservableObject
{
    public int Id { get; } = id;
    public string FullPath { get; } = fullPath;
    public string FileName { get; } = fileName;
    // Anfitrião de onde vem (o endereço original da transferência); null quando não se sabe.
    public string? Source { get; } = source;
    private readonly (string Head, string Tail) _name = DownloadFormat.SplitName(fileName);
    public string NameHead => _name.Head;
    public string NameTail => _name.Tail;
    // Quando a transferência acabou: o painel ignora cliques no segundo seguinte (DownloadRisk.AcceptsClick).
    public DateTime? CompletedAtUtc { get; private set; }

    [ObservableProperty] private long _received;
    [ObservableProperty] private long _total;
    [ObservableProperty] private long _speed;
    [ObservableProperty] private bool _isComplete;
    [ObservableProperty] private bool _isCancelled;

    partial void OnIsCompleteChanged(bool value) => CompletedAtUtc = value ? DateTime.UtcNow : null;

    public bool IsActive => !IsComplete && !IsCancelled;

    public int Percent => Total > 0 ? (int)Math.Clamp(Received * 100 / Total, 0, 100) : 0;

    public string Status => IsCancelled ? "Cancelada"
        : IsComplete ? DownloadFormat.Size(Received)
        : DownloadFormat.Progress(Received, Total, Speed);

    internal void Refresh()
    {
        OnPropertyChanged(nameof(Percent));
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(IsActive));
    }
}

public partial class DownloadsViewModel : ObservableObject
{
    public ObservableCollection<DownloadEntry> Items { get; } = new();

    public bool HasActive => Items.Any(i => i.IsActive);

    public int OverallPercent
    {
        get
        {
            var active = Items.Where(i => i.IsActive).ToList();
            return active.Count == 0 ? 0 : (int)active.Average(i => i.Percent);
        }
    }

    public void Started(int id, string fullPath, string fileName, string? source = null)
    {
        Items.Insert(0, new DownloadEntry(id, fullPath, fileName, source));
        Changed();
    }

    public void Update(int id, long received, long total, long speed, bool complete, bool cancelled)
    {
        var entry = Items.FirstOrDefault(i => i.Id == id);
        if (entry is null) return;
        entry.Received = received;
        entry.Total = total;
        entry.Speed = speed;
        entry.IsComplete = complete;
        entry.IsCancelled = cancelled;
        entry.Refresh();
        Changed();
    }

    private void Changed()
    {
        OnPropertyChanged(nameof(HasActive));
        OnPropertyChanged(nameof(OverallPercent));
    }
}
