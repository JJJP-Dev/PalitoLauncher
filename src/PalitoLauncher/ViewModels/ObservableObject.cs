using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace PalitoLauncher.ViewModels;

/// <summary>Base mínima de INotifyPropertyChanged (MVVM hecho a mano).</summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Asigna el campo y notifica si el valor cambió. Devuelve true si cambió.</summary>
    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    /// <summary>Notifica un cambio. Con null o "" WPF refresca todas las propiedades.</summary>
    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
