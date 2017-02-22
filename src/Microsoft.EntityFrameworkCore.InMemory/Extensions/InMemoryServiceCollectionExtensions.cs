// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Infrastructure.Internal;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.ExpressionVisitors;
using Microsoft.EntityFrameworkCore.Query.ExpressionVisitors.Internal;
using Microsoft.EntityFrameworkCore.Query.Internal;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.Storage.Internal;
using Microsoft.EntityFrameworkCore.Utilities;
using Microsoft.EntityFrameworkCore.ValueGeneration;
using Microsoft.EntityFrameworkCore.ValueGeneration.Internal;
using Microsoft.Extensions.DependencyInjection.Extensions;

// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.DependencyInjection
{
    /// <summary>
    ///     In-memory specific extension methods for <see cref="IServiceCollection" />.
    /// </summary>
    public static class InMemoryServiceCollectionExtensions
    {
        /// <summary>
        ///     <para>
        ///         Adds the services required by the in-memory database provider for Entity Framework
        ///         to an <see cref="IServiceCollection" />. You use this method when using dependency injection
        ///         in your application, such as with ASP.NET. For more information on setting up dependency
        ///         injection, see http://go.microsoft.com/fwlink/?LinkId=526890.
        ///     </para>
        ///     <para>
        ///         You only need to use this functionality when you want Entity Framework to resolve the services it uses
        ///         from an external dependency injection container. If you are not using an external
        ///         dependency injection container, Entity Framework will take care of creating the services it requires.
        ///     </para>
        /// </summary>
        /// <example>
        ///     <code>
        ///         public void ConfigureServices(IServiceCollection services)
        ///         {
        ///             services
        ///                 .AddEntityFrameworkInMemoryDatabase()
        ///                 .AddDbContext&lt;MyContext&gt;((serviceProvider, options) =>
        ///                     options.UseInMemoryDatabase()
        ///                            .UseInternalServiceProvider(serviceProvider));
        ///         }
        ///     </code>
        /// </example>
        /// <param name="serviceCollection"> The <see cref="IServiceCollection" /> to add services to. </param>
        /// <returns>
        ///     The same service collection so that multiple calls can be chained.
        /// </returns>
        public static IServiceCollection AddEntityFrameworkInMemoryDatabase([NotNull] this IServiceCollection serviceCollection)
        {
            Check.NotNull(serviceCollection, nameof(serviceCollection));

            var serviceCollectionMap = new ServiceCollectionMap(serviceCollection)
                .TryAddSingletonEnumerable<IDatabaseProvider, DatabaseProvider<InMemoryOptionsExtension>>()
                .TryAddSingleton<IInMemoryStoreSource, InMemoryStoreSource>()
                .TryAddSingleton<IInMemoryTableFactory, InMemoryTableFactory>()
                .TryAddScoped<IValueGeneratorSelector, InMemoryValueGeneratorSelector>()
                .TryAddScoped<IInMemoryDatabase, InMemoryDatabase>()
                .TryAddScoped<IDatabase>(p => p.GetService<IInMemoryDatabase>())
                .TryAddScoped<IDbContextTransactionManager, InMemoryTransactionManager>()
                .TryAddScoped<IDatabaseCreator, InMemoryDatabaseCreator>()
                .TryAddScoped<IMaterializerFactory, MaterializerFactory>()
                .TryAddScoped<IQueryContextFactory, InMemoryQueryContextFactory>()
                .TryAddScoped<IEntityQueryModelVisitorFactory, InMemoryQueryModelVisitorFactory>()
                .TryAddScoped<IEntityQueryableExpressionVisitorFactory, InMemoryEntityQueryableExpressionVisitorFactory>()
                .TryAddScoped<IInMemoryResultOperatorHandler, InMemoryResultOperatorHandler>();

            ServiceCollectionProviderInfrastructure.TryAddDefaultEntityFrameworkServices(serviceCollectionMap);

            return serviceCollection;
        }
    }
}
