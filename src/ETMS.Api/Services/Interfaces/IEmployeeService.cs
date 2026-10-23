using ETMS.Api.DTOs.Requests;
using ETMS.Api.DTOs.Responses;

namespace ETMS.Api.Services.Interfaces;

public interface IEmployeeService
{
    Task<UserDto> CreateEmployeeAsync(CreateEmployeeRequest request, CancellationToken cancellationToken = default);
    Task<PagedResult<EmployeeListDto>> GetEmployeesAsync(EmployeeFilterParams filter, CancellationToken cancellationToken = default);
}
