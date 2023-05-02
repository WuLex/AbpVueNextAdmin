﻿using BaseService.BaseData;
using BaseService.Systems.UserManagement.Dto;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Volo.Abp.Application.Dtos;
using Volo.Abp.Application.Services;
using Volo.Abp.Domain.Repositories;
using Volo.Abp.Identity;
using Volo.Abp.ObjectExtending;

namespace BaseService.Systems.UserManagement
{
    [Authorize(IdentityPermissions.Users.Default)]
    public class UserAppService : ApplicationService, IUserAppService
    {
        protected IdentityUserManager UserManager { get; }
        protected IIdentityUserRepository UserRepository { get; }
        public IIdentityRoleRepository RoleRepository { get; }
        private readonly IRepository<Organization, Guid> _orgRepository;
        private readonly IRepository<UserJob> _userJobsRepository;
        private readonly IRepository<UserOrganization> _userOrgsRepository;

        public UserAppService(
            IdentityUserManager userManager,
            IIdentityUserRepository userRepository,
            IIdentityRoleRepository roleRepository,
            IRepository<Organization, Guid> orgRepository,
            IRepository<UserJob> userJobsRepository,
            IRepository<UserOrganization> userOrgsRepository
            )
        {
            UserManager = userManager;
            UserRepository = userRepository;
            RoleRepository = roleRepository;
            _orgRepository = orgRepository;
            _userJobsRepository = userJobsRepository;
            _userOrgsRepository = userOrgsRepository;
        }

        public async Task<BaseIdentityUserDto> Get(Guid id)
        {
            var dto = ObjectMapper.Map<IdentityUser, BaseIdentityUserDto>(await UserManager.GetByIdAsync(id));
            var jobIds = await (await _userJobsRepository.GetQueryableAsync()).Where(_ => _.UserId == id).Select(_ => _.JobId).ToListAsync();
            var orgIds = await (await _userOrgsRepository.GetQueryableAsync()).Where(_ => _.UserId == id).Select(_ => _.OrganizationId).ToListAsync();
            dto.JobIds = jobIds;
            dto.OrganizationIds = orgIds;
            dto.RoleNames = await UserRepository.GetRoleNamesAsync(id);

            return dto;
        }

        [Authorize(IdentityPermissions.Users.Create)]
        public async Task<IdentityUserDto> Create(BaseIdentityUserCreateDto input)
        {
            var user = new IdentityUser(
                GuidGenerator.Create(),
                input.UserName,
                input.Email,
                CurrentTenant.Id
            );

            input.MapExtraPropertiesTo(user);

            (await UserManager.CreateAsync(user, input.Password)).CheckErrors();
            await UpdateUserByInput(user, input);

            var dto = ObjectMapper.Map<IdentityUser, IdentityUserDto>(user);

            if (input.JobIds != null && input.JobIds.Count > 0)
            {
                foreach (var id in input.JobIds)
                {
                    await _userJobsRepository.InsertAsync(new UserJob(CurrentTenant.Id, user.Id, id));
                }
            }
            
            if(input.OrganizationIds!=null&& input.OrganizationIds.Count>0)
            {
                foreach (var id in input.OrganizationIds)
                {
                    await _userOrgsRepository.InsertAsync(new UserOrganization(CurrentTenant.Id, user.Id, id));
                }
            }

            await CurrentUnitOfWork.SaveChangesAsync();

            return dto;
        }

        [Authorize(IdentityPermissions.Users.Update)]
        public async Task<IdentityUserDto> UpdateAsync(Guid id, BaseIdentityUserUpdateDto input)
        {
            var user = await UserManager.GetByIdAsync(id);
            user.ConcurrencyStamp = input.ConcurrencyStamp;

            (await UserManager.SetUserNameAsync(user, input.UserName)).CheckErrors();

            await UpdateUserByInput(user, input);
            input.MapExtraPropertiesTo(user);

            (await UserManager.UpdateAsync(user)).CheckErrors();

            if (!input.Password.IsNullOrEmpty())
            {
                (await UserManager.RemovePasswordAsync(user)).CheckErrors();
                (await UserManager.AddPasswordAsync(user, input.Password)).CheckErrors();
            }

            var dto = ObjectMapper.Map<IdentityUser, IdentityUserDto>(user);

            await _userJobsRepository.DeleteAsync(_ => _.UserId == id);
            if (input.JobIds != null && input.JobIds.Count > 0)
            {
                foreach (var jid in input.JobIds)
                {
                    await _userJobsRepository.InsertAsync(new UserJob(CurrentTenant.Id, id, jid));
                }
            }
               

            await _userOrgsRepository.DeleteAsync(_ => _.UserId == id);
            if (input.OrganizationIds != null && input.OrganizationIds.Count > 0)
            {
                foreach (var oid in input.OrganizationIds)
                {
                    await _userOrgsRepository.InsertAsync(new UserOrganization(CurrentTenant.Id, id, oid));
                }
            }
               
            await CurrentUnitOfWork.SaveChangesAsync();

            return dto;
        }

        public async Task<PagedResultDto<BaseIdentityUserDto>> GetAll(GetBaseIdentityUsersInput input)
        {
            var orgQueryable = await _orgRepository.GetQueryableAsync();
            var userOrgQueryable = await _userOrgsRepository.GetQueryableAsync();
            if (input.OrganizationId.HasValue)
            {
                var userDbSet = await UserRepository.GetDbSetAsync();
                var org = await _orgRepository.GetAsync(input.OrganizationId.Value);
                var orgs = await (await _orgRepository.GetQueryableAsync()).Where(_ => _.CascadeId.Contains(org.CascadeId)).ToListAsync();

                var totalCount = await userOrgQueryable.Where(_ => orgs.Select(o => o.Id).Contains(_.OrganizationId))
                                                     .GroupBy(_ => _.UserId)
                                                     .LongCountAsync();

                //TODO: Redis Query
                var userIds = await userOrgQueryable.Where(_ => orgs.Select(o => o.Id).Contains(_.OrganizationId))
                                                        .Select(_ => _.UserId)
                                                        .Distinct()
                                                        .Skip(input.SkipCount)
                                                        .Take(input.MaxResultCount)
                                                        .ToListAsync();
                
                var items = await userDbSet.WhereIf(!string.IsNullOrWhiteSpace(input.Filter), _ => _.UserName.Contains(input.Filter))
                                           .Where(_ => userIds.Contains(_.Id)).ToListAsync();
                var userOrgs = await userOrgQueryable.Where(_ => items.Select(i => i.Id).Contains(_.UserId))
                                        .ToListAsync();
                var allOrg = await orgQueryable.Where(_ => userOrgs.Select(uo => uo.OrganizationId).Contains(_.Id))
                                               .OrderBy(_ => _.CascadeId)
                                               .ToListAsync();
                var dtos = ObjectMapper.Map<List<IdentityUser>, List<BaseIdentityUserDto>>(items);

                foreach (var dto in dtos)
                {
                    var oids = userOrgs.Where(_ => _.UserId == dto.Id).Select(_ => _.OrganizationId);
                    dto.OrganizationNames = string.Join(", ", allOrg.Where(_ => oids.Contains(_.Id)).Select(_ => _.Name).ToArray());
                }
                return new PagedResultDto<BaseIdentityUserDto>(totalCount, dtos);
            }
            else
            {
                var totalCount = await UserRepository.GetCountAsync(input.Filter);
                var items = await UserRepository.GetListAsync(input.Sorting, input.MaxResultCount, input.SkipCount, input.Filter);
                //TODO: Redis Query
                var userOrgs = await userOrgQueryable.Where(_ => items.Select(i => i.Id).Contains(_.UserId))
                                                        .ToListAsync();
                var orgs = await orgQueryable.Where(_ => userOrgs.Select(uo => uo.OrganizationId).Contains(_.Id))
                                               .OrderBy(_ => _.CascadeId)
                                               .ToListAsync();
                var dtos = ObjectMapper.Map<List<IdentityUser>, List<BaseIdentityUserDto>>(items);
                foreach (var dto in dtos)
                {
                    var oids = userOrgs.Where(_ => _.UserId == dto.Id).Select(_ => _.OrganizationId);
                    dto.OrganizationNames = string.Join(", ", orgs.Where(_ => oids.Contains(_.Id)).Select(_ => _.Name).ToArray());
                }
                return new PagedResultDto<BaseIdentityUserDto>(totalCount, dtos);
            }
        }

        protected virtual async Task UpdateUserByInput(IdentityUser user, IdentityUserCreateOrUpdateDtoBase input)
        {
            if (!string.Equals(user.Email, input.Email, StringComparison.InvariantCultureIgnoreCase))
            {
                (await UserManager.SetEmailAsync(user, input.Email)).CheckErrors();
            }

            if (!string.Equals(user.PhoneNumber, input.PhoneNumber, StringComparison.InvariantCultureIgnoreCase))
            {
                (await UserManager.SetPhoneNumberAsync(user, input.PhoneNumber)).CheckErrors();
            }

            (await UserManager.SetLockoutEnabledAsync(user, input.LockoutEnabled)).CheckErrors();

            user.Name = input.Name;
            user.Surname = input.Surname;

            if (input.RoleNames != null)
            {
                (await UserManager.SetRolesAsync(user, input.RoleNames)).CheckErrors();
            }
        }
    }
}
