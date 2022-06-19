namespace GameHook.Domain.Interfaces
{
    public interface ITransformer<T>
    {
        static abstract T ToValue(byte[] data);
        static abstract byte[] FromValue(T value);
    }
}
