namespace YARG.Online.Lobbies.Commands;

public interface ICommandHandler<TCommand>
{
    Task Handle(TCommand command, CancellationToken ct);
}
