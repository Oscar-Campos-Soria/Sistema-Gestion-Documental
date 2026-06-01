using MediatR;
using QualityDMS.Application.Common.Exceptions;
using QualityDMS.Domain.Common;
using QualityDMS.Domain.Entities;
using QualityDMS.Domain.Enums;
using QualityDMS.Domain.Interfaces;

namespace QualityDMS.Application.Workflow.Commands.ApproveStep;

public class ApproveStepCommandHandler(
    IDocumentRepository documentRepository,
    IWorkflowRepository workflowRepository,
    INotificationService notificationService,
    ICurrentUserService currentUser,
    IUnitOfWork uow,
    IPublicDmsWebhookService webhook,
    IPhpSyncService phpSync) : IRequestHandler<ApproveStepCommand, Result>
{
    public async Task<Result> Handle(ApproveStepCommand cmd, CancellationToken ct)
    {
        var document = await documentRepository.GetByIdWithVersionsAsync(cmd.DocumentId, ct)
            ?? throw new NotFoundException(nameof(Document), cmd.DocumentId);

        var instance = await workflowRepository.GetActiveInstanceByDocumentAsync(cmd.DocumentId, ct)
            ?? throw new NotFoundException(nameof(WorkflowInstance), cmd.DocumentId);

        var template = await workflowRepository.GetTemplateByIdAsync(instance.WorkflowTemplateId, ct)
            ?? throw new NotFoundException(nameof(WorkflowTemplate), instance.WorkflowTemplateId);

        var action = new WorkflowAction
        {
            WorkflowInstanceId = instance.WorkflowInstanceId,
            StepOrder = instance.CurrentStepOrder,
            ActionByUserId = currentUser.UserId,
            Action = WorkflowStepStatus.Approved,
            Comments = cmd.Comments,
            ActionDate = DateTime.UtcNow,
            CreatedBy = currentUser.UserName
        };
        await workflowRepository.AddActionAsync(action, ct);

        var steps = template.Steps.OrderBy(s => s.StepOrder).ToList();
        var nextStep = steps.FirstOrDefault(s => s.StepOrder > instance.CurrentStepOrder);
        var fullyApproved = nextStep is null;

        DocumentVersion? approvedVersion = null;
        if (fullyApproved)
        {
            instance.Status = WorkflowStepStatus.Approved;
            instance.CompletedAt = DateTime.UtcNow;
            workflowRepository.UpdateInstance(instance);
            // Sella el borrador en revisión como nueva versión aprobada X.0 (inmutable)
            // y obsoleta automáticamente la vigente anterior.
            approvedVersion = document.Approve(currentUser.UserId);
            documentRepository.Update(document);

            await notificationService.SendAsync(
                document.CreatedBy,
                NotificationType.DocumentApproved,
                $"Documento aprobado: {document.Code}",
                $"El documento '{document.Title}' ha sido aprobado.",
                document.DocumentId, ct);
        }
        else
        {
            instance.CurrentStepOrder = nextStep!.StepOrder;
            workflowRepository.UpdateInstance(instance);

            if (nextStep.AssignedUserId is not null)
            {
                await notificationService.SendAsync(
                    nextStep.AssignedUserId,
                    NotificationType.WorkflowStepAssigned,
                    $"Pendiente de aprobación: {document.Code}",
                    $"El documento '{document.Title}' requiere su aprobación (Paso {nextStep.StepOrder}).",
                    document.DocumentId, ct);
            }
        }

        await uow.SaveChangesAsync(ct);

        // Enviar eventos a APIs (PostgreSQL + MongoDB) después de guardar en DB
        if (fullyApproved)
        {
            try
            {
                // Datos de la versión recién sellada (X.0). Solo versiones aprobadas
                // se publican a PHP/Mongo (los borradores X.Y nunca salen de .NET).
                var categoryName = document.Category?.Name ?? "Unknown";
                var departmentName = document.Department?.Name ?? "Unknown";
                var companyName = document.Company?.Name ?? "Unknown";
                var versionNumber = approvedVersion?.VersionNumber ?? "1.0";
                var fileUrl = approvedVersion?.FilePath ?? string.Empty;

                // API 1: Sincronizar documento a PostgreSQL (con empresa = aislamiento multiempresa)
                await phpSync.ApproveDocumentAsync(
                    document.DocumentId,
                    document.Code,
                    document.Title,
                    document.CategoryId,
                    categoryName,
                    document.DepartmentId,
                    departmentName,
                    versionNumber,
                    fileUrl,
                    document.EffectiveDate,
                    document.ExpirationDate,
                    approvedVersion?.ApprovedAt,
                    document.CompanyId,
                    companyName);

                // API 2: Indexar metadatos + contenido (full-text) en MongoDB.
                // Push completo: FastAPI no consulta SQL, lee el archivo del volumen compartido.
                await webhook.NotifyDocumentApprovedAsync(
                    document.DocumentId,
                    document.Code,
                    document.Title,
                    categoryName,
                    departmentName,
                    versionNumber,
                    fileUrl,
                    document.CompanyId,
                    companyName);
            }
            catch (Exception ex)
            {
                // Log error pero no bloquear (APIs async, failure no crítico)
                // En producción, guardar en tabla de reintentos
            }
        }

        return Result.Success();
    }
}

