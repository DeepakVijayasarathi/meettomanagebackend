using iucs.meettomanage.application.Common;
using iucs.meettomanage.domain.Entities.Settings;
using iucs.meettomanage.domain.Enums;
using iucs.meettomanage.domain.Repository;
using Microsoft.EntityFrameworkCore;

namespace iucs.meettomanage.api.Services
{
    /// <summary>
    /// Which store new uploads go to right now: S3 or this server's disk. Chosen per client on
    /// Admin → Settings → File storage and kept in the database ("storage.provider" AppSetting), so
    /// it switches live without a redeploy; until that row exists, Storage:Provider in configuration
    /// decides. Without S3 credentials it is always Local -- there is nowhere else to put files.
    /// </summary>
    public class StorageProviderSwitch
    {
        private volatile bool _local;
        private readonly IServiceScopeFactory? _scopes;
        private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, (bool Local, DateTime Loaded)> _tenantChoices = new();

        public StorageProviderSwitch(IConfiguration configuration, IServiceScopeFactory? scopes = null)
        {
            _scopes = scopes;
            _local = IsLocal(configuration["Storage:Provider"]);
            S3Configured = S3FileStorage.IsConfigured(configuration);
        }

        public bool S3Configured { get; }

        public bool UseLocal => !S3Configured || TenantChoice();

        private bool TenantChoice()
        {
            if (TenantExecution.Current is not { } tenant || _scopes is null) return _local;
            if (_tenantChoices.TryGetValue(tenant.Id, out var cached) && cached.Loaded > DateTime.UtcNow.AddSeconds(-15)) return cached.Local;
            using var scope = _scopes.CreateScope();
            scope.ServiceProvider.GetRequiredService<iucs.meettomanage.api.Tenancy.TenantResolver>()
                .ResolveCodeAsync(tenant.Code).GetAwaiter().GetResult();
            var saved = scope.ServiceProvider.GetRequiredService<IUnitOfWork>().Repository<AppSetting>().Query()
                .Where(s => s.Key == StorageSettings.ProviderKey).Select(s => s.Value).FirstOrDefault();
            var local = string.IsNullOrWhiteSpace(saved) ? _local : IsLocal(saved);
            _tenantChoices[tenant.Id] = (local, DateTime.UtcNow);
            return local;
        }

        /// <summary>"S3" or "Local" -- the store actually in use.</summary>
        public string Provider => UseLocal ? StorageSettings.Local : StorageSettings.S3;

        /// <summary>Picks up the saved choice at startup (after the database is ready).</summary>
        public async Task LoadAsync(IServiceProvider services, CancellationToken cancellationToken = default)
        {
            using var scope = services.CreateScope();
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var saved = await unitOfWork.Repository<AppSetting>().Query()
                .Where(s => s.Key == StorageSettings.ProviderKey)
                .Select(s => s.Value)
                .FirstOrDefaultAsync(cancellationToken);
            if (!string.IsNullOrWhiteSpace(saved))
            {
                _local = IsLocal(saved);
            }
        }

        /// <summary>Saves the choice and applies it at once (the caller stages the audit entry).</summary>
        public async Task SetAsync(bool local, IUnitOfWork unitOfWork, CancellationToken cancellationToken = default)
        {
            var value = local ? StorageSettings.Local : StorageSettings.S3;
            var repository = unitOfWork.Repository<AppSetting>();
            var setting = await repository.FirstOrDefaultAsync(s => s.Key == StorageSettings.ProviderKey, cancellationToken);
            if (setting is null)
            {
                await repository.AddAsync(
                    new AppSetting { Key = StorageSettings.ProviderKey, Value = value, Category = SettingCategory.General },
                    cancellationToken);
            }
            else
            {
                setting.Value = value;
                repository.Update(setting);
            }

            await unitOfWork.SaveChangesAsync(cancellationToken);
            if (TenantExecution.Current is { } tenant) _tenantChoices[tenant.Id] = (local, DateTime.UtcNow);
            else _local = local;
        }

        private static bool IsLocal(string? provider) =>
            string.Equals(provider?.Trim(), StorageSettings.Local, StringComparison.OrdinalIgnoreCase);
    }
}
