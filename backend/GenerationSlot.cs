namespace Studio;

public sealed class GenerationSlot
{
    private int occupied;
    public bool Enter() => Interlocked.CompareExchange(ref occupied, 1, 0) == 0;
    public void Leave() => Volatile.Write(ref occupied, 0);
}
