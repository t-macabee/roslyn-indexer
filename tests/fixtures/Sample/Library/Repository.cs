namespace Library;

/// <summary>Generic base class for repositories. Exercises structural edge extraction.</summary>
public class Repository<T> where T : class
{
    protected readonly List<T> _items = [];

    public void Add(T item) => _items.Add(item);

    public T? Get(int index) => index >= 0 && index < _items.Count ? _items[index] : null;
}
