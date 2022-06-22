using GameHook.Application;
using GameHook.WebAPI.Controllers;
using Mapster;

namespace GameHook.WebAPI
{
    public static class Mapping
    {
        public static void Setup()
        {
            TypeAdapterConfig.GlobalSettings.Default.PreserveReference(true);

            TypeAdapterConfig.GlobalSettings.RequireDestinationMemberSource = true;

            TypeAdapterConfig<Mapper, MapperModel>.NewConfig()
                .Map(dest => dest.Meta, src => src.Metadata);

            TypeAdapterConfig<GameHookProperty, PropertyModel>.NewConfig()
                .Map(dest => dest.Path, src => src.Path)
                .Map(dest => dest.Type, src => src.Type)
                .Map(dest => dest.Address, src => src.Address)
                .Map(dest => dest.Size, src => src.Size)
                .Map(dest => dest.Position, src => src.MapperVariables.Position)
                .Map(dest => dest.Reference, src => src.MapperVariables.Reference)
                .Map(dest => dest.Value, src => src.Value)
                .Map(dest => dest.Bytes, src => src.Bytes)
                .Map(dest => dest.Frozen, src => src.IsFrozen)
                .Map(dest => dest.Description, src => src.MapperVariables.Description);
        }
    }
}
