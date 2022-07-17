namespace GameHook.Domain
{
    public class SanityCheckException : Exception
    {
        public SanityCheckException(string message) : base(message) { }
    }

    public abstract class UserPresentableException : Exception
    {
        public UserPresentableException(string title, Exception? innerException) : base(title, innerException)
        {
            Title = title;
        }

        public string Title { get; }
    }

    public class DriverTimeoutException : Exception
    {
        public MemoryAddress MemoryAddress { get; }

        public DriverTimeoutException(MemoryAddress address, string driverName, Exception? innerException)
            : base($"A timeout occurred when reading address {address.ToHexdecimalString()}. Is {driverName} running and accessible?", innerException)
        {
            MemoryAddress = address;
        }
    }

    public class DriverErrorException :Exception
    {
        public DriverErrorException(string message) : base(message) { }
    }

    public class GenericUserPresentableException : UserPresentableException
    {
        public GenericUserPresentableException(string message) : base(message, null) { }
    }

    public class GameHookContainerInitializationException : UserPresentableException
    {
        public GameHookContainerInitializationException(string message, Exception? innerException) : base(message, innerException) { }
    }

    public class MapperParsingException : UserPresentableException
    {
        public MapperParsingException(string filename, Exception innerException)
            : base($"Could not parse {filename}.\nCheck the mapper to determine if there are any errors in the format of the file.", innerException) { }
    }

    public class DriverShutdownException : Exception
    {
        public DriverShutdownException(Exception? innerException)
            : base("RetroArch driver is shutting down due to lost connection.", innerException) { }
    }
}
