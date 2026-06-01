using MediatR;
using QualityDMS.Application.Common.Exceptions;
using QualityDMS.Domain.Common;
using QualityDMS.Domain.Entities;
using QualityDMS.Domain.Enums;
using QualityDMS.Domain.Interfaces;

namespace QualityDMS.Application.Documents.Commands.UpdateDocument;

public class UpdateDocumentCommandHandler(
    IDocumentRepository documentRepository,
    IFileStorageService fileStorage,
    ICurrentUserService currentUser,
    IUnitOfWork uow) : IRequestHandler<UpdateDocumentCommand, Result>
{
    public async Task<Result> Handle(UpdateDocumentCommand cmd, CancellationToken ct)
    {
        var document = await documentRepository.GetByIdWithVersionsAsync(cmd.DocumentId, ct)
            ?? throw new NotFoundException(nameof(Document), cmd.DocumentId);

        var editableCycle = document.Status is DocumentStatus.Draft or DocumentStatus.Rejected;
        var approved = document.Status == DocumentStatus.Approved;

        if (!editableCycle && !approved)
            return Result.Failure("Solo documentos en borrador, rechazados o aprobados (para nueva revisión) pueden editarse.");

        // Los metadatos solo se editan en el ciclo de borrador. En un documento ya
        // aprobado la versión vigente es inmutable: editar = iniciar una nueva revisión
        // (nuevo borrador X.1), sin mutar la versión vigente.
        if (editableCycle)
        {
            document.Title = cmd.Title;
            document.Description = cmd.Description;
            document.CategoryId = cmd.CategoryId;
            document.DepartmentId = cmd.DepartmentId;
            document.WorkflowTemplateId = cmd.WorkflowTemplateId;
            document.NextReviewDate = cmd.NextReviewDate;
        }
        document.UpdatedBy = currentUser.UserName;

        var hasNewFile = cmd.FileStream is not null && cmd.FileName is not null && cmd.ContentType is not null;

        if (approved && !hasNewFile)
            return Result.Failure("Para revisar un documento aprobado debe adjuntar el archivo de la nueva versión.");

        if (hasNewFile)
        {
            var (filePath, sizeBytes) = await fileStorage.UploadAsync(
                cmd.FileStream!, cmd.FileName!, cmd.ContentType!, ct);

            // El número (0.x o X.y) lo asigna el agregado según el major aprobado actual.
            document.AddDraftVersion(
                filePath, cmd.FileName!, sizeBytes, cmd.ContentType!, currentUser.UserId, cmd.ChangeLog);
        }

        documentRepository.Update(document);
        await uow.SaveChangesAsync(ct);
        return Result.Success();
    }
}

