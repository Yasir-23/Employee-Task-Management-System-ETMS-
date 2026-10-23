using ETMS.Api.DTOs.Requests;
using ETMS.Api.DTOs.Responses;

namespace ETMS.Api.Services.Interfaces;

public interface IDepartmentService
{
    Task<DepartmentDto> CreateDepartmentAsync(CreateDepartmentRequest request, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DepartmentDto>> GetAllDepartmentsAsync(CancellationToken cancellationToken = default);
}
