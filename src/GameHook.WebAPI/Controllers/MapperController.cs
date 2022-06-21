using GameHook.Domain;
using GameHook.Domain.GameHookProperties;
using GameHook.Domain.Interfaces;
using Mapster;
using Microsoft.AspNetCore.Mvc;
using Swashbuckle.AspNetCore.Annotations;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GameHook.WebAPI.Controllers
{
    static class MapperHelper
    {
        public static PropertyModel MapToPropertyModel(this IGameHookProperty x, string path) =>
            new PropertyModel
            {
                Path = path,
                Type = x.Type,
                Address = x.Address,
                Size = x.Size,
                Position = x.Fields.Position,
                Reference = x.Fields.Reference,
                Value = x.Value,
                Frozen = x.Frozen,
                Bytes = x.Bytes.ToIntegerArray(),
                Description = x.Fields.Description
            };
    }

    public record MapperModel(MapperMetaModel Meta, IEnumerable<PropertyModel> Properties, Dictionary<string, IEnumerable<GlossaryItemModel>> Glossary);
    public record MapperMetaModel(int SchemaVersion, Guid Id, string GameName, string GamePlatform);
    public class GlossaryItemModel
    {
        public uint Key { get; init; }
        public object? Value { get; init; }
    }

    public record MapperReplaceModel(string? Id);

    public class PropertyModel
    {
        public string Path { get; init; } = string.Empty;

        public string Type { get; init; } = string.Empty;

        public uint Address { get; init; }

        public int Size { get; init; }

        public int? Position { get; init; }

        public string? Reference { get; init; }

        [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
        public object? Value { get; init; }

        public IEnumerable<int> Bytes { get; init; } = Enumerable.Empty<int>();

        public bool? Frozen { get; init; }

        public string? Description { get; init; }
    }

    public class UpdatePropertyModel
    {
        public JsonElement? Value { get; init; }
        public int[]? Bytes { get; init; }
        public bool? Freeze { get; init; }
    }

    [ApiController]
    [Produces("application/json")]
    [Consumes("application/json")]
    [Route("mapper")]
    public class MapperController : ControllerBase
    {
        public IGameHookContainerFactory GameMapperFactory { get; }
        public IGameHookContainer? GameHookMapper => GameMapperFactory.LoadedMapper;

        public MapperController(IGameHookContainerFactory gameMapperFactory)
        {
            GameMapperFactory = gameMapperFactory;
        }

        [HttpGet]
        [SwaggerOperation("Returns the mapper that was loaded, with all properties (populated with data).")]
        public ActionResult<MapperModel> GetMapper()
        {
            if (GameMapperFactory.LoadedMapper == null)
                return ApiHelper.MapperNotLoaded();

            var model = GameMapperFactory.LoadedMapper.Adapt<MapperModel>();
            return Ok(model);
        }

        [HttpPut]
        [SwaggerOperation("Changes the active mapper.")]
        public async Task<ActionResult> ChangeMapper(MapperReplaceModel model)
        {
            if (string.IsNullOrEmpty(model.Id))
            {
                await GameMapperFactory.ReloadGameMapper();
            }
            else
            {
                await GameMapperFactory.LoadGameMapper(model.Id);
            }

            var count = GameMapperFactory.LoadedMapper.Properties;

            return Ok();
        }

        [HttpGet("meta")]
        [SwaggerOperation("Returns the meta section of the mapper file.")]
        public ActionResult<MapperMetaModel> GetMeta()
        {
            if (GameHookMapper == null || GameHookMapper.Meta == null)
                return ApiHelper.MapperNotLoaded();

            var meta = GameHookMapper.Meta;
            var model = new MapperMetaModel(meta.SchemaVersion, meta.Id, meta.GameName, meta.GamePlatform);

            return Ok(model);
        }

        [HttpGet("values/{**path}/")]
        [Produces("text/plain")]
        [SwaggerOperation("Returns a specific property's value by it's path.")]
        public ActionResult GetValueAsync(string path)
        {
            if (GameHookMapper == null)
                return ApiHelper.MapperNotLoaded();

            path = path.StripEndingRoute().FromRouteToPath();

            var prop = GameHookMapper.GetPropertyByPath(path);
            if (prop == null)
                return NotFound();

            if (prop.Value is object)
            {
                return BadRequest($"{prop.Identifier} is an object and cannot be converted to text.");
            }

            return Ok(prop.Value?.ToString() ?? string.Empty);
        }

        [HttpGet("properties")]
        [SwaggerOperation("Returns all properties loaded from the mapper.")]
        public ActionResult<IEnumerable<PropertyModel>> GetProperties()
        {
            if (GameHookMapper == null)
                return ApiHelper.MapperNotLoaded();

            return Ok(GameHookMapper.Properties.Select(x => x.Value.MapToPropertyModel(x.Key)));
        }

        [HttpGet("properties/{**path}/")]
        [SwaggerOperation("Returns a specific property by it's path.")]
        public ActionResult<PropertyModel?> GetProperty(string path)
        {
            if (GameHookMapper == null)
                return ApiHelper.MapperNotLoaded();

            path = path.StripEndingRoute().FromRouteToPath();

            var prop = GameHookMapper.GetPropertyByPath(path);
            if (prop == null)
                return NotFound();

            return Ok(prop.MapToPropertyModel(path));
        }

        [HttpPut("properties/{**path}/")]
        [SwaggerOperation("Updates a property's value.")]
        public async Task<ActionResult> UpdatePropertyAsync(string path, UpdatePropertyModel model)
        {
            if (GameHookMapper == null)
                return ApiHelper.MapperNotLoaded();

            path = path.StripEndingRoute().FromRouteToPath();
            var prop = GameHookMapper.GetPropertyByPath(path);

            if (prop == null)
                return NotFound();

            if (model.Value == null && model.Bytes == null && model.Freeze == null)
                return BadRequest("Invalid arguments.");

            if (model.Value != null)
            {
                if (prop.Type == "bcd")
                {
                    var prop2 = (BinaryCodedDecimalProperty)prop;
                    await prop2.WriteValue(model.Value.Value.GetInt32(), model.Freeze);
                }
                else if (prop.Type == "bit")
                {
                    throw new NotImplementedException();
                }
                else if (prop.Type == "boolean")
                {
                    var prop2 = (BooleanProperty)prop;
                    await prop2.WriteValue(model.Value.Value.GetBoolean(), model.Freeze);
                }
                else if (prop.Type == "int")
                {
                    var prop2 = (IntegerProperty)prop;
                    await prop2.WriteValue(model.Value.Value.GetInt32(), model.Freeze);
                }
                else if (prop.Type == "reference")
                {
                    throw new NotImplementedException();
                }
                else if (prop.Type == "string")
                {
                    var prop2 = (StringProperty)prop;
                    await prop2.WriteValue(model.Value.Value.GetString(), model.Freeze);
                }
                else if (prop.Type == "uint")
                {
                    var prop2 = (UnsignedIntegerProperty)prop;
                    await prop2.WriteValue(model.Value.Value.GetUInt32(), model.Freeze);
                }
                else
                {
                    return BadRequest("Invalid value.");
                }
            }
            else if (model.Bytes != null)
            {
                await prop.WriteBytes(model.Bytes.Select(x => (byte)x).ToArray(), model.Freeze);
            }
            else if (model.Freeze == false)
            {
                prop.Unfreeze();
            }

            foreach (var notifier in GameHookMapper.ClientNotifiers)
            {
                if (model.Freeze == true) { await notifier.SendPropertyFrozen(path); }
                else { await notifier.SendPropertyUnfrozen(path); }
            }

            return Ok();
        }

        [HttpGet("glossary")]
        [SwaggerOperation("Returns the glossary section of the mapper file.")]
        public ActionResult<Dictionary<string, Dictionary<byte, dynamic>>> GetGlossary()
        {
            if (GameHookMapper == null)
                return ApiHelper.MapperNotLoaded();

            return Ok(GameHookMapper.Glossary);
        }

        [HttpGet("glossary/{key}")]
        [SwaggerOperation("Returns a specific glossary by it's key.")]
        public ActionResult<Dictionary<byte, dynamic>> GetGlossaryPage(string key)
        {
            if (GameHookMapper == null)
                return ApiHelper.MapperNotLoaded();

            key = key.StripEndingRoute();

            if (GameHookMapper.Glossary.ContainsKey(key) == false)
                return NotFound();
            else
                return Ok(GameHookMapper.Glossary[key]);
        }
    }
}