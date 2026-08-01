using System;
using FFS.Libraries.StaticEcs;

namespace UniGame.StaticEcs.Network
{
    internal interface ISchemaInvoker
    {
        Type RuntimeType { get; }
    }

    internal interface IEntityInvoker : ISchemaInvoker
    {
    }

    internal interface IEntityInvoker<TWorld> : IEntityInvoker where TWorld : struct, IWorldType
    {
        World<TWorld>.Entity Create(EntityGID entity);
    }

    internal interface IEntryCodec : ISchemaInvoker
    {
        bool Validate(ReadOnlySpan<byte> payload, uint count);
    }

    internal interface ICommandInvoker : IEntryCodec
    {
        Type AuthorizerType { get; }
        DispatchResult Dispatch(ReadOnlySpan<byte> payload, in CommandContext context);
    }

    internal interface ICommandInvoker<T> : ICommandInvoker where T : unmanaged
    {
        bool TryAuthorize(ReadOnlySpan<byte> payload, in CommandContext context, out T command);
    }

    internal sealed class EntityInvoker<TWorld, TEntityType> : IEntityInvoker<TWorld>
        where TWorld : struct, IWorldType
        where TEntityType : unmanaged, IEntityType
    {
        public Type RuntimeType => typeof(TEntityType);

        public World<TWorld>.Entity Create(EntityGID entity) => World<TWorld>.NewEntityByGID<TEntityType>(entity);
    }

    internal sealed class ComponentInvoker<TWorld, T, TCodec> : IEntryCodec
        where TWorld : struct, IWorldType
        where T : unmanaged, IComponent
        where TCodec : struct, ICodec<T>
    {
        public Type RuntimeType => typeof(T);

        public bool Validate(ReadOnlySpan<byte> payload, uint count)
        {
            var codec = default(TCodec);
            return count == 1 && codec.TryRead(payload, out _, out var read) && read == payload.Length;
        }
    }

    internal sealed class TagInvoker<TWorld, T> : IEntryCodec
        where TWorld : struct, IWorldType
        where T : unmanaged, ITag
    {
        public Type RuntimeType => typeof(T);

        public bool Validate(ReadOnlySpan<byte> payload, uint count) => count == 0 && payload.IsEmpty;
    }

    internal sealed class LinkInvoker<TWorld, T> : IEntryCodec
        where TWorld : struct, IWorldType
        where T : unmanaged, ILinkType
    {
        public Type RuntimeType => typeof(T);

        public bool Validate(ReadOnlySpan<byte> payload, uint count) => count == 1 && payload.Length == 8;
    }

    internal sealed class LinksInvoker<TWorld, T> : IEntryCodec
        where TWorld : struct, IWorldType
        where T : unmanaged, ILinksType
    {
        public Type RuntimeType => typeof(T);

        public bool Validate(ReadOnlySpan<byte> payload, uint count) => payload.Length == count * 8L;
    }

    internal sealed class MultiInvoker<TWorld, T, TCodec> : IEntryCodec
        where TWorld : struct, IWorldType
        where T : unmanaged, IMultiComponent
        where TCodec : struct, ICodec<T>
    {
        private readonly uint _maxItemBytes;

        internal MultiInvoker(uint maxItemBytes) => _maxItemBytes = maxItemBytes;

        public Type RuntimeType => typeof(T);

        public bool Validate(ReadOnlySpan<byte> payload, uint count)
        {
            var offset = 0;
            var codec = default(TCodec);
            for (var i = 0; i < count; i++)
            {
                if (offset > payload.Length - 4) return false;
                var length = Hashing.Read32(payload, offset);
                offset += 4;
                if (length > _maxItemBytes || length > payload.Length - offset ||
                    !codec.TryRead(payload.Slice(offset, (int)length), out _, out var read) || read != length)
                    return false;
                offset += (int)length;
            }
            return offset == payload.Length;
        }
    }

    internal sealed class CommandInvoker<TWorld, T, TCodec, TAuthorizer> : ICommandInvoker<T>
        where TWorld : struct, IWorldType
        where T : unmanaged
        where TCodec : struct, ICodec<T>
        where TAuthorizer : struct, ICommandAuthorizer<TWorld, T>
    {
        public Type RuntimeType => typeof(T);
        public Type AuthorizerType => typeof(TAuthorizer);

        public bool Validate(ReadOnlySpan<byte> payload, uint count)
        {
            var codec = default(TCodec);
            return count == 1 && codec.TryRead(payload, out _, out var read) && read == payload.Length;
        }

        public bool TryAuthorize(ReadOnlySpan<byte> payload, in CommandContext context, out T command)
        {
            var codec = default(TCodec);
            if (!codec.TryRead(payload, out command, out var read) || read != payload.Length) return false;
            var authorizer = default(TAuthorizer);
            return authorizer.Authorize(in context, in command);
        }

        public DispatchResult Dispatch(ReadOnlySpan<byte> payload, in CommandContext context)
        {
            var codec = default(TCodec);
            if (!codec.TryRead(payload, out var command, out var read) || read != payload.Length)
                return DispatchResult.InvalidCommand;
            if (!World<TWorld>.IsEventTypeRegistered<CommandAcceptedEvent<T>>() ||
                !World<TWorld>.IsEventTypeRegistered<CommandRejectedEvent<T>>())
                return DispatchResult.ConfigurationError;

            var authorizer = default(TAuthorizer);
            var accepted = authorizer.Authorize(in context, in command);
            var sent = accepted
                ? World<TWorld>.SendEvent(new CommandAcceptedEvent<T> { Command = command, Context = context })
                : World<TWorld>.SendEvent(new CommandRejectedEvent<T> { Command = command, Context = context });
            if (!sent) return DispatchResult.NoReceiver;
            return accepted ? DispatchResult.Accepted : DispatchResult.Rejected;
        }
    }
}
