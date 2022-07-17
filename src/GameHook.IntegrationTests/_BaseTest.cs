using Newtonsoft.Json.Linq;
using OpenAPI.GameHook;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using A = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace GameHook.IntegrationTests
{
    public static class Assert
    {
        public static void AreValuesEqual(long expected, object actual)
        {
            A.AreEqual(expected, (long)actual);
        }

        public static void AreValuesEqual(bool expected, object actual)
        {
            A.AreEqual(expected, (bool)actual);
        }

        public static void AreValueArraysEqual(List<bool> expected, JArray actual)
        {
            var actualConverted = actual.ToObject<List<bool>>() ?? throw new Exception("Unable to convert JArray.");
            A.AreEqual(string.Join(", ", expected), string.Join(", ", actualConverted));
        }

        public static void ArePropertiesEqual(PropertyModel expected, PropertyModel actual)
        {
            A.AreEqual(expected.Address, actual.Address);
            AreBytesEqual(expected.Bytes, actual.Bytes);
            A.AreEqual(expected.Description, actual.Description);
            A.AreEqual(expected.Frozen, actual.Frozen);
            A.AreEqual(expected.Path, actual.Path);
            A.AreEqual(expected.Position, actual.Position);
            A.AreEqual(expected.Reference, actual.Reference);
            A.AreEqual(expected.Size, actual.Size);
            A.AreEqual(expected.Type, actual.Type);
            A.AreEqual(expected.Value, actual.Value);
        }

        public static void AreBytesEqual(ICollection<int> expected, ICollection<int> actual)
        {
            A.AreEqual(string.Join(' ', expected), string.Join(' ', actual));
        }

        public static void AreEqual(object expected, object? actual)
        {
            A.AreEqual(expected, actual);
        }

        public static void IsTrue(bool o)
        {
            A.IsTrue(o);
        }

        public static void IsFalse(bool o)
        {
            A.IsFalse(o);
        }

        public static void AreNotEqual(object notExpected, object actual)
        {
            A.AreNotEqual(notExpected, actual);
        }

        public static void IsNotNull(object? o)
        {
            A.IsNotNull(o);
        }

        public static void IsNull(object? o)
        {
            A.IsNull(o);
        }
    }

    public abstract class BaseTest
    {
        protected GameHookClient GameHookClient { get; }

        public BaseTest()
        {
            var httpClient = new HttpClient();
            GameHookClient = new GameHookClient("http://localhost:8085", httpClient);
        }

        public async Task Load_GB_PokemonYellow()
        {
            await GameHookClient.ChangeMapperAsync(new MapperReplaceModel()
            {
                Id = "ff4d0e23c73b21068ef1f5deffb6b6ea"
            });
        }
    }
}
