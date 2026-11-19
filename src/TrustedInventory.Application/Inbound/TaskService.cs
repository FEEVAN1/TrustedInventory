using FluentValidation;
using Microsoft.EntityFrameworkCore;
using TrustedInventory.Application.Common;
using TrustedInventory.Application.Errors;
using TrustedInventory.Application.Persistence;
using TrustedInventory.Application.Security;
using TrustedInventory.Domain.Inbound;
using TrustedInventory.Domain.Security;
using TaskStatus = TrustedInventory.Domain.Inbound.TaskStatus;

namespace TrustedInventory.Application.Inbound;

public sealed record WorkTaskFilter(Guid? WarehouseId, TaskType? Type, TaskStatus? Status);

public sealed record CompleteTask(string Resolution);

public sealed class CompleteTaskValidator : AbstractValidator<CompleteTask>
{
    public CompleteTaskValidator() => RuleFor(x => x.Resolution).ValidReason();
}

/// <summary>
/// Warehouse tasks of every type (DEC-011, DEC-016, DEC-085): putaway, rework (OUT-006) and exception (INB-005). Rework and exception
/// tasks close by hand once their stock has left the source bin; nothing here moves stock.
/// </summary>
public sealed class TaskService(ITenantUnitOfWork uow, ICallerAccessor callers, ICursorCodec cursors, IValidator<CompleteTask> validator)
{
    public Task<Page<WarehouseTaskDto>> ListAsync(WorkTaskFilter filter, PageRequest page, CancellationToken ct)
    {
        var scope = callers.Caller.WarehouseFilter(Permissions.StockRead);
        return uow.ReadAsync(async (s, c) =>
        {
            var query = s.Db.Set<WarehouseTask>().AsNoTracking();
            if (scope is not null)
                query = query.Where(t => scope.Contains(t.WarehouseId));
            if (filter.WarehouseId is { } w)
                query = query.Where(t => t.WarehouseId == w);
            if (filter.Type is { } type)
                query = query.Where(t => t.Type == type);
            if (filter.Status is { } status)
                query = query.Where(t => t.Status == status);
            var tasks = await query.ToPageAsync(page, $"tasks|{filter.WarehouseId}|{filter.Type}|{filter.Status}", cursors, t => t, c);
            var dtos = new List<WarehouseTaskDto>();
            foreach (var task in tasks.Items)
                dtos.Add(await PutawayService.DtoAsync(s, task, c, withRecommendations: false));
            return new Page<WarehouseTaskDto>(dtos, tasks.NextCursor);
        }, ct);
    }

    public Task<WarehouseTaskDto> GetAsync(Guid id, CancellationToken ct)
    {
        var caller = callers.Caller;
        caller.Require(Permissions.StockRead);
        return uow.ReadAsync(async (s, c) =>
        {
            var task = await s.Db.Set<WarehouseTask>().AsNoTracking().SingleOrDefaultAsync(t => t.Id == id, c) ?? throw AppException.NotFound("Task");
            caller.RequireWarehouse(Permissions.StockRead, task.WarehouseId);
            return await PutawayService.DtoAsync(s, task, c);
        }, ct);
    }

    /// <summary>
    /// Completes an open rework or exception task with a resolution (OUT-006, INB-005). Refused while the task's product and lot
    /// still sit in its source bin, so a task cannot be closed with its stock left behind. A receipt closes when this was the
    /// last open task of its lines (DEC-005).
    /// </summary>
    public Task<WarehouseTaskDto> CompleteAsync(Guid id, int expectedVersion, CompleteTask cmd, CancellationToken ct)
    {
        var caller = callers.Caller;
        caller.Require(Permissions.TasksManage);
        caller.RequireMutable();
        validator.ValidateOrThrow(cmd);
        return uow.WriteAsync(async (s, c) =>
        {
            var locked = await s.Db.Database.SqlQuery<Guid>($"SELECT id AS \"Value\" FROM ti.warehouse_task WHERE id = {id} FOR UPDATE").ToListAsync(c);
            if (locked.Count == 0)
                throw AppException.NotFound("Task");
            var task = await s.Db.Set<WarehouseTask>().SingleAsync(t => t.Id == id, c);
            caller.RequireWarehouse(Permissions.TasksManage, task.WarehouseId);
            Versioning.EnsureVersion(task.Version, expectedVersion);
            if (task.Type == TaskType.Putaway)
                throw AppException.InvalidState("A putaway task completes through putaway confirmation.");
            if (!task.IsOpen)
                throw AppException.InvalidState("Only an open task can be completed.");
            if (task.SourceBinId is { } bin && task.ProductId is { } product)
            {
                var lot = task.LotId;
                var left = await s.Db.Set<StockBucket>().AsNoTracking()
                    .AnyAsync(b => b.BinId == bin && b.ProductId == product && b.LotId == lot && b.OnHandQty > 0, c);
                if (left)
                    throw AppException.InvalidState("The task's stock is still in its source bin. Move it before completing the task.");
            }

            var before = await PutawayService.DtoAsync(s, task, c, withRecommendations: false);
            DomainGuard.Run(() => task.Complete(cmd.Resolution, caller.Subject, s.Now));
            await s.Db.SaveChangesAsync(c);
            if (task.ReceiptLineId is { } lineId)
            {
                var receiptId = await s.Db.Set<ReceiptLine>().Where(l => l.Id == lineId).Select(l => l.ReceiptId).SingleAsync(c);
                await PutawayService.CloseReceiptIfDoneAsync(s, receiptId, c);
            }
            var after = await PutawayService.DtoAsync(s, task, c, withRecommendations: false);
            s.Audit(new AuditEntry("TaskCompleted", "WarehouseTask", task.Id, task.Version, cmd.Resolution, before, after));
            await s.Db.SaveChangesAsync(c);
            return after;
        }, ct);
    }
}
