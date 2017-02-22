// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Internal;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Internal;
using Microsoft.EntityFrameworkCore.Query.Expressions.Internal;
using Microsoft.EntityFrameworkCore.Query.ExpressionVisitors;
using Microsoft.EntityFrameworkCore.Query.ExpressionVisitors.Internal;
using Microsoft.EntityFrameworkCore.Query.Internal;
using Microsoft.EntityFrameworkCore.Query.ResultOperators.Internal;
using Microsoft.EntityFrameworkCore.Utilities;
using Microsoft.Extensions.Logging;
using Remotion.Linq;
using Remotion.Linq.Clauses;
using Remotion.Linq.Clauses.Expressions;
using Remotion.Linq.Clauses.ExpressionVisitors;
using Remotion.Linq.Clauses.ResultOperators;
using Remotion.Linq.Clauses.StreamedData;
using Remotion.Linq.Parsing;

namespace Microsoft.EntityFrameworkCore.Query
{
    /// <summary>
    ///     <para>
    ///         The core visitor that processes a query to be executed.
    ///     </para>
    ///     <para>
    ///         This type is typically used by database providers (and other extensions). It is generally
    ///         not used in application code.
    ///     </para>
    /// </summary>
    public abstract class EntityQueryModelVisitor : QueryModelVisitorBase
    {
        /// <summary>
        ///     Expression to reference the <see cref="QueryContext" /> parameter for a query.
        /// </summary>
        public static readonly ParameterExpression QueryContextParameter
            = Expression.Parameter(typeof(QueryContext), "queryContext");

        private static readonly string _efTypeName = typeof(EF).FullName;

        /// <summary>
        ///     Determines if a <see cref="MethodInfo" /> is referencing the <see cref="EF.Property{TProperty}(object, string)" /> method.
        /// </summary>
        /// <param name="methodInfo"> The method info to check. </param>
        /// <returns>
        ///     True if <paramref name="methodInfo" /> is referencing <see cref="EF.Property{TProperty}(object, string)" />; otherwise fale;
        /// </returns>
        public static bool IsPropertyMethod([CanBeNull] MethodInfo methodInfo) =>
            Equals(methodInfo, EF.PropertyMethod)
            ||
            (
                // fallback to string comparison because MethodInfo.Equals is not
                // always true in .NET Native even if methods are the same
                methodInfo != null
                && methodInfo.IsGenericMethod
                && methodInfo.Name == nameof(EF.Property)
                && methodInfo.DeclaringType?.FullName == _efTypeName
            );

        /// <summary>
        ///     Creates an expression to access the given property on an given entity.
        /// </summary>
        /// <param name="target"> The entity. </param>
        /// <param name="property"> The property to be accessed. </param>
        /// <returns> The newly created expression. </returns>
        public static Expression CreatePropertyExpression(
            [NotNull] Expression target, [NotNull] IPropertyBase property)
            => Expression.Call(
                null,
                EF.PropertyMethod.MakeGenericMethod(property.ClrType.MakeNullable()),
                target,
                Expression.Constant(property.Name));

        private readonly IQueryOptimizer _queryOptimizer;
        private readonly INavigationRewritingExpressionVisitorFactory _navigationRewritingExpressionVisitorFactory;
        private readonly ISubQueryMemberPushDownExpressionVisitor _subQueryMemberPushDownExpressionVisitor;
        private readonly IQuerySourceTracingExpressionVisitorFactory _querySourceTracingExpressionVisitorFactory;
        private readonly IEntityResultFindingExpressionVisitorFactory _entityResultFindingExpressionVisitorFactory;
        private readonly ITaskBlockingExpressionVisitor _taskBlockingExpressionVisitor;
        private readonly IMemberAccessBindingExpressionVisitorFactory _memberAccessBindingExpressionVisitorFactory;
        private readonly IProjectionExpressionVisitorFactory _projectionExpressionVisitorFactory;
        private readonly IEntityQueryableExpressionVisitorFactory _entityQueryableExpressionVisitorFactory;
        private readonly IQueryAnnotationExtractor _queryAnnotationExtractor;
        private readonly IResultOperatorHandler _resultOperatorHandler;
        private readonly IEntityMaterializerSource _entityMaterializerSource;
        private readonly IExpressionPrinter _expressionPrinter;
        private readonly QueryCompilationContext _queryCompilationContext;

        private Expression _expression;
        private ParameterExpression _currentParameter;

        private int _transparentParameterCounter;

        // TODO: Can these be non-blocking?
        private bool _blockTaskExpressions = true;

        /// <summary>
        ///     Initializes a new instance of the <see cref="EntityQueryModelVisitor" /> class.
        /// </summary>
        /// <param name="queryOptimizer"> The <see cref="IQueryOptimizer" /> to be used when processing the query. </param>
        /// <param name="navigationRewritingExpressionVisitorFactory">
        ///     The <see cref="INavigationRewritingExpressionVisitorFactory" /> to be used when
        ///     processing the query.
        /// </param>
        /// <param name="subQueryMemberPushDownExpressionVisitor">
        ///     The <see cref="ISubQueryMemberPushDownExpressionVisitor" /> to be used when
        ///     processing the query.
        /// </param>
        /// <param name="querySourceTracingExpressionVisitorFactory">
        ///     The <see cref="IQuerySourceTracingExpressionVisitorFactory" /> to be used when
        ///     processing the query.
        /// </param>
        /// <param name="entityResultFindingExpressionVisitorFactory">
        ///     The <see cref="IEntityResultFindingExpressionVisitorFactory" /> to be used when
        ///     processing the query.
        /// </param>
        /// <param name="taskBlockingExpressionVisitor"> The <see cref="ITaskBlockingExpressionVisitor" /> to be used when processing the query. </param>
        /// <param name="memberAccessBindingExpressionVisitorFactory">
        ///     The <see cref="IMemberAccessBindingExpressionVisitorFactory" /> to be used when
        ///     processing the query.
        /// </param>
        /// <param name="orderingExpressionVisitorFactory"> The <see cref="IOrderingExpressionVisitorFactory" /> to be used when processing the query. </param>
        /// <param name="projectionExpressionVisitorFactory">
        ///     The <see cref="IProjectionExpressionVisitorFactory" /> to be used when processing the
        ///     query.
        /// </param>
        /// <param name="entityQueryableExpressionVisitorFactory">
        ///     The <see cref="IEntityQueryableExpressionVisitorFactory" /> to be used when
        ///     processing the query.
        /// </param>
        /// <param name="queryAnnotationExtractor"> The <see cref="IQueryAnnotationExtractor" /> to be used when processing the query. </param>
        /// <param name="resultOperatorHandler"> The <see cref="IResultOperatorHandler" /> to be used when processing the query. </param>
        /// <param name="entityMaterializerSource"> The <see cref="IEntityMaterializerSource" /> to be used when processing the query. </param>
        /// <param name="expressionPrinter"> The <see cref="IExpressionPrinter" /> to be used when processing the query. </param>
        /// <param name="queryCompilationContext"> The <see cref="QueryCompilationContext" /> to be used when processing the query. </param>
        protected EntityQueryModelVisitor(
            [NotNull] IQueryOptimizer queryOptimizer,
            [NotNull] INavigationRewritingExpressionVisitorFactory navigationRewritingExpressionVisitorFactory,
            [NotNull] ISubQueryMemberPushDownExpressionVisitor subQueryMemberPushDownExpressionVisitor,
            [NotNull] IQuerySourceTracingExpressionVisitorFactory querySourceTracingExpressionVisitorFactory,
            [NotNull] IEntityResultFindingExpressionVisitorFactory entityResultFindingExpressionVisitorFactory,
            [NotNull] ITaskBlockingExpressionVisitor taskBlockingExpressionVisitor,
            [NotNull] IMemberAccessBindingExpressionVisitorFactory memberAccessBindingExpressionVisitorFactory,
            [NotNull] IOrderingExpressionVisitorFactory orderingExpressionVisitorFactory,
            [NotNull] IProjectionExpressionVisitorFactory projectionExpressionVisitorFactory,
            [NotNull] IEntityQueryableExpressionVisitorFactory entityQueryableExpressionVisitorFactory,
            [NotNull] IQueryAnnotationExtractor queryAnnotationExtractor,
            [NotNull] IResultOperatorHandler resultOperatorHandler,
            [NotNull] IEntityMaterializerSource entityMaterializerSource,
            [NotNull] IExpressionPrinter expressionPrinter,
            [NotNull] QueryCompilationContext queryCompilationContext)
        {
            Check.NotNull(queryOptimizer, nameof(queryOptimizer));
            Check.NotNull(navigationRewritingExpressionVisitorFactory, nameof(navigationRewritingExpressionVisitorFactory));
            Check.NotNull(subQueryMemberPushDownExpressionVisitor, nameof(subQueryMemberPushDownExpressionVisitor));
            Check.NotNull(querySourceTracingExpressionVisitorFactory, nameof(querySourceTracingExpressionVisitorFactory));
            Check.NotNull(entityResultFindingExpressionVisitorFactory, nameof(entityResultFindingExpressionVisitorFactory));
            Check.NotNull(taskBlockingExpressionVisitor, nameof(taskBlockingExpressionVisitor));
            Check.NotNull(memberAccessBindingExpressionVisitorFactory, nameof(memberAccessBindingExpressionVisitorFactory));
            Check.NotNull(orderingExpressionVisitorFactory, nameof(orderingExpressionVisitorFactory));
            Check.NotNull(projectionExpressionVisitorFactory, nameof(projectionExpressionVisitorFactory));
            Check.NotNull(entityQueryableExpressionVisitorFactory, nameof(entityQueryableExpressionVisitorFactory));
            Check.NotNull(queryAnnotationExtractor, nameof(queryAnnotationExtractor));
            Check.NotNull(resultOperatorHandler, nameof(resultOperatorHandler));
            Check.NotNull(entityMaterializerSource, nameof(entityMaterializerSource));
            Check.NotNull(expressionPrinter, nameof(expressionPrinter));
            Check.NotNull(queryCompilationContext, nameof(queryCompilationContext));

            _queryOptimizer = queryOptimizer;
            _navigationRewritingExpressionVisitorFactory = navigationRewritingExpressionVisitorFactory;
            _subQueryMemberPushDownExpressionVisitor = subQueryMemberPushDownExpressionVisitor;
            _querySourceTracingExpressionVisitorFactory = querySourceTracingExpressionVisitorFactory;
            _entityResultFindingExpressionVisitorFactory = entityResultFindingExpressionVisitorFactory;
            _taskBlockingExpressionVisitor = taskBlockingExpressionVisitor;
            _memberAccessBindingExpressionVisitorFactory = memberAccessBindingExpressionVisitorFactory;
            _projectionExpressionVisitorFactory = projectionExpressionVisitorFactory;
            _entityQueryableExpressionVisitorFactory = entityQueryableExpressionVisitorFactory;
            _queryAnnotationExtractor = queryAnnotationExtractor;
            _resultOperatorHandler = resultOperatorHandler;
            _entityMaterializerSource = entityMaterializerSource;
            _expressionPrinter = expressionPrinter;
            _queryCompilationContext = queryCompilationContext;

            LinqOperatorProvider = queryCompilationContext.LinqOperatorProvider;
        }

        /// <summary>
        ///     Gets the expression that represents this query.
        /// </summary>
        public virtual Expression Expression
        {
            get { return _expression; }
            [param: NotNull]
            set
            {
                Check.NotNull(value, nameof(value));

                _expression = value;
            }
        }

        /// <summary>
        ///     Gets the expression for the current parameter.
        /// </summary>
        public virtual ParameterExpression CurrentParameter
        {
            get { return _currentParameter; }
            [param: NotNull]
            set
            {
                Check.NotNull(value, nameof(value));

                _currentParameter = value;
            }
        }

        /// <summary>
        ///     Gets the <see cref="Query.QueryCompilationContext" /> being used for this query.
        /// </summary>
        public virtual QueryCompilationContext QueryCompilationContext => _queryCompilationContext;

        /// <summary>
        ///     Gets the <see cref="ILinqOperatorProvider" /> being used for this query.
        /// </summary>
        public virtual ILinqOperatorProvider LinqOperatorProvider { get; private set; }

        /// <summary>
        ///     Creates an action to execute this query.
        /// </summary>
        /// <typeparam name="TResult"> The type of results that the query returns. </typeparam>
        /// <param name="queryModel"> The query. </param>
        /// <returns> An action that returns the results of the query. </returns>
        public virtual Func<QueryContext, IEnumerable<TResult>> CreateQueryExecutor<TResult>([NotNull] QueryModel queryModel)
        {
            Check.NotNull(queryModel, nameof(queryModel));

            using (QueryCompilationContext.Logger.BeginScope(this))
            {
                QueryCompilationContext.Logger
                    .LogDebug(
                        CoreEventId.CompilingQueryModel,
                        () => CoreStrings.LogCompilingQueryModel(Environment.NewLine, queryModel.Print()));

                _blockTaskExpressions = false;

                ExtractQueryAnnotations(queryModel);

                var includeResultOperators
                    = QueryCompilationContext.QueryAnnotations
                        .OfType<IncludeResultOperator>()
                        .ToList();

                OptimizeQueryModel(queryModel, includeResultOperators);

                QueryCompilationContext.FindQuerySourcesRequiringMaterialization(this, queryModel);
                QueryCompilationContext.DetermineQueryBufferRequirement(queryModel);

                VisitQueryModel(queryModel);

                SingleResultToSequence(queryModel);

                IncludeNavigations(queryModel, includeResultOperators);

                TrackEntitiesInResults<TResult>(queryModel);

                InterceptExceptions();

                return CreateExecutorLambda<IEnumerable<TResult>>();
            }
        }
        
        /// <summary>
        ///     Creates an action to asynchronously execute this query.
        /// </summary>
        /// <typeparam name="TResult"> The type of results that the query returns. </typeparam>
        /// <param name="queryModel"> The query. </param>
        /// <returns> An action that asynchronously returns the results of the query. </returns>
        public virtual Func<QueryContext, IAsyncEnumerable<TResult>> CreateAsyncQueryExecutor<TResult>([NotNull] QueryModel queryModel)
        {
            Check.NotNull(queryModel, nameof(queryModel));

            using (QueryCompilationContext.Logger.BeginScope(this))
            {
                QueryCompilationContext.Logger
                    .LogDebug(
                        CoreEventId.CompilingQueryModel,
                        () => CoreStrings.LogCompilingQueryModel(Environment.NewLine, queryModel.Print()));

                _blockTaskExpressions = false;

                ExtractQueryAnnotations(queryModel);

                var includeResultOperators
                    = QueryCompilationContext.QueryAnnotations
                        .OfType<IncludeResultOperator>()
                        .ToList();

                OptimizeQueryModel(queryModel, includeResultOperators);

                QueryCompilationContext.FindQuerySourcesRequiringMaterialization(this, queryModel);
                QueryCompilationContext.DetermineQueryBufferRequirement(queryModel);

                VisitQueryModel(queryModel);

                SingleResultToSequence(queryModel, _expression.Type.GetTypeInfo().GenericTypeArguments[0]);

                IncludeNavigations(queryModel, includeResultOperators);

                TrackEntitiesInResults<TResult>(queryModel);

                InterceptExceptions();

                return CreateExecutorLambda<IAsyncEnumerable<TResult>>();
            }
        }

        /// <summary>
        ///     Executes the query and logs any exceptions that occur.
        /// </summary>
        protected virtual void InterceptExceptions()
            => _expression
                = Expression.Call(
                    LinqOperatorProvider.InterceptExceptions
                        .MakeGenericMethod(_expression.Type.GetSequenceType()),
                    _expression,
                    Expression.Constant(QueryCompilationContext.ContextType),
                    Expression.Constant(QueryCompilationContext.Logger),
                    QueryContextParameter);

        /// <summary>
        ///     Populates <see cref="Query.QueryCompilationContext.QueryAnnotations" /> based on annotations found in the query.
        /// </summary>
        /// <param name="queryModel"> The query. </param>
        protected virtual void ExtractQueryAnnotations([NotNull] QueryModel queryModel)
        {
            Check.NotNull(queryModel, nameof(queryModel));

            QueryCompilationContext.QueryAnnotations
                = _queryAnnotationExtractor.ExtractQueryAnnotations(queryModel);
        }

        /// <summary>
        ///     Applies optimizations to the query.
        /// </summary>
        /// <param name="queryModel"> The query. </param>
        /// <param name="includeResultOperators">TODO: This parameter is to be removed.</param>
        protected virtual void OptimizeQueryModel(
            [NotNull] QueryModel queryModel, 
            [NotNull] ICollection<IncludeResultOperator> includeResultOperators)
        {
            Check.NotNull(queryModel, nameof(queryModel));
            Check.NotNull(includeResultOperators, nameof(includeResultOperators));

            _queryOptimizer.Optimize(QueryCompilationContext.QueryAnnotations, queryModel);

            var entityEqualityRewritingExpressionVisitor
                = new EntityEqualityRewritingExpressionVisitor(QueryCompilationContext.Model);

            entityEqualityRewritingExpressionVisitor.Rewrite(queryModel);

            queryModel.TransformExpressions(_subQueryMemberPushDownExpressionVisitor.Visit);

            new NondeterministicResultCheckingVisitor(QueryCompilationContext.Logger)
                .VisitQueryModel(queryModel);

            new IncludeCompiler(
                    QueryCompilationContext,
                    _querySourceTracingExpressionVisitorFactory)
                .CompileIncludes(queryModel, includeResultOperators, TrackResults(queryModel));

            _navigationRewritingExpressionVisitorFactory
                .Create(this).Rewrite(queryModel, parentQueryModel: null);

            QueryCompilationContext.Logger
                .LogDebug(
                    CoreEventId.OptimizedQueryModel,
                    () => CoreStrings.LogOptimizedQueryModel(Environment.NewLine, queryModel.Print()));
        }

        private class NondeterministicResultCheckingVisitor : QueryModelVisitorBase
        {
            private const int QueryModelStringLengthLimit = 100;
            private readonly ILogger _logger;

            public NondeterministicResultCheckingVisitor([NotNull] ILogger logger)
            {
                _logger = logger;
            }

            public override void VisitQueryModel(QueryModel queryModel)
            {
                if (queryModel.ResultOperators.Any(o => o is SkipResultOperator || o is TakeResultOperator)
                    && !queryModel.BodyClauses.OfType<OrderByClause>().Any())
                {
                    _logger.LogWarning(
                        CoreEventId.CompilingQueryModel,
                        () => CoreStrings.RowLimitingOperationWithoutOrderBy(
                            queryModel.Print(removeFormatting: true, characterLimit: QueryModelStringLengthLimit)));
                }

                if (queryModel.ResultOperators.Any(o => o is FirstResultOperator)
                    && !queryModel.BodyClauses.OfType<OrderByClause>().Any()
                    && !queryModel.BodyClauses.OfType<WhereClause>().Any())
                {
                    _logger.LogWarning(
                        CoreEventId.CompilingQueryModel,
                        () => CoreStrings.FirstWithoutOrderByAndFilter(
                            queryModel.Print(removeFormatting: true, characterLimit: QueryModelStringLengthLimit)));
                }

                queryModel.TransformExpressions(new RecursiveQueryModelExpressionVisitor(this).Visit);
            }


            private class RecursiveQueryModelExpressionVisitor : ExpressionVisitorBase
            {
                private readonly NondeterministicResultCheckingVisitor _parentVisitor;

                public RecursiveQueryModelExpressionVisitor(NondeterministicResultCheckingVisitor parentVisitor)
                {
                    _parentVisitor = parentVisitor;
                }

                protected override Expression VisitSubQuery(SubQueryExpression expression)
                {
                    _parentVisitor.VisitQueryModel(expression.QueryModel);

                    return base.VisitSubQuery(expression);
                }
            }
        }

        /// <summary>
        ///     Converts the results of the query from a single result to a series of results.
        /// </summary>
        /// <param name="queryModel"> The query. </param>
        /// <param name="type"> The type of results returned by the query. </param>
        protected virtual void SingleResultToSequence([NotNull] QueryModel queryModel, [CanBeNull] Type type = null)
        {
            Check.NotNull(queryModel, nameof(queryModel));

            if (!(queryModel.GetOutputDataInfo() is StreamedSequenceInfo))
            {
                _expression
                    = Expression.Call(
                        LinqOperatorProvider.ToSequence
                            .MakeGenericMethod(type ?? _expression.Type),
                        _expression);
            }
        }

        /// <summary>
        ///     Includes related data requested in the LINQ query.
        /// </summary>
        /// <param name="queryModel"> The query. </param>
        /// <param name="includeResultOperators"></param>
        protected virtual void IncludeNavigations(
            [NotNull] QueryModel queryModel,
            [NotNull] ICollection<IncludeResultOperator> includeResultOperators)
        {
            Check.NotNull(queryModel, nameof(queryModel));

            if (queryModel.GetOutputDataInfo() is StreamedScalarValueInfo)
            {
                return;
            }

            var includeSpecifications
                = includeResultOperators
                    .Select(includeResultOperator =>
                        {
                            var entityType = QueryCompilationContext.Model.FindEntityType(
                                includeResultOperator.PathFromQuerySource.Type);

                            var parts = includeResultOperator.NavigationPropertyPaths.ToArray();
                            var navigationPath = new INavigation[parts.Length];
                            for (var i = 0; i < parts.Length; i++)
                            {
                                navigationPath[i] = entityType.FindNavigation(parts[i]);

                                if (navigationPath[i] == null)
                                {
                                    throw new InvalidOperationException(
                                        CoreStrings.IncludeBadNavigation(parts[i], entityType.DisplayName()));
                                }

                                entityType = navigationPath[i].GetTargetType();
                            }

                            return new
                            {
                                specification = new IncludeSpecification(includeResultOperator.QuerySource, navigationPath),
                                order = string.Concat(navigationPath.Select(n => n.IsCollection() ? "1" : "0"))
                            };
                        })
                    .OrderByDescending(e => e.order)
                    .ThenBy(e => e.specification.NavigationPath.First().IsDependentToPrincipal())
                    .Select(e => e.specification)
                    .ToList();

            IncludeNavigations(queryModel, includeSpecifications);
        }

        /// <summary>
        ///     Includes related data requested in the LINQ query.
        /// </summary>
        /// <param name="queryModel"> The query. </param>
        /// <param name="includeSpecifications"> Related data to be included. </param>
        protected virtual void IncludeNavigations(
            [NotNull] QueryModel queryModel,
            [NotNull] IReadOnlyCollection<IncludeSpecification> includeSpecifications)
        {
            Check.NotNull(queryModel, nameof(queryModel));
            Check.NotNull(includeSpecifications, nameof(includeSpecifications));

            foreach (var includeSpecification in includeSpecifications)
            {
                var resultQuerySourceReferenceExpression
                    = _querySourceTracingExpressionVisitorFactory
                        .Create()
                        .FindResultQuerySourceReferenceExpression(
                            queryModel.SelectClause.Selector,
                            includeSpecification.QuerySource);

                if (resultQuerySourceReferenceExpression != null)
                {
                    var accessorExpression = QueryCompilationContext.QuerySourceMapping.GetExpression(
                        resultQuerySourceReferenceExpression.ReferencedQuerySource);

                    var sequenceType = resultQuerySourceReferenceExpression.Type.TryGetSequenceType();

                    if (sequenceType != null
                        && QueryCompilationContext.Model.FindEntityType(sequenceType) != null)
                    {
                        includeSpecification.IsEnumerableTarget = true;
                    }

                    QueryCompilationContext.Logger
                        .LogDebug(
                            CoreEventId.IncludingNavigation,
                            () => CoreStrings.LogIncludingNavigation(includeSpecification));

                    IncludeNavigations(
                        includeSpecification,
                        _expression.Type.GetSequenceType(),
                        accessorExpression,
                        QueryCompilationContext.IsTrackingQuery);

                    QueryCompilationContext
                        .AddTrackableInclude(
                            resultQuerySourceReferenceExpression.ReferencedQuerySource,
                            includeSpecification.NavigationPath);
                }
                else
                {
                    QueryCompilationContext.Logger
                        .LogWarning(
                            CoreEventId.IncludeIgnoredWarning,
                            () => CoreStrings.LogIgnoredInclude(includeSpecification));
                }
            }
        }

        /// <summary>
        ///     Includes a specific navigation property requested in the LINQ query.
        /// </summary>
        /// <param name="includeSpecification"> The navigation property to be included. </param>
        /// <param name="resultType"> The type of results returned by the query. </param>
        /// <param name="accessorExpression"> Expression for the navigation property to be included. </param>
        /// <param name="querySourceRequiresTracking"> A value indicating whether results of this query are to be tracked. </param>
        protected virtual void IncludeNavigations(
            [NotNull] IncludeSpecification includeSpecification,
            [NotNull] Type resultType,
            [NotNull] Expression accessorExpression,
            bool querySourceRequiresTracking)
        {
            // template method
            throw new NotImplementedException(CoreStrings.IncludeNotImplemented);
        }

        /// <summary>
        ///     Applies tracking behavior to the query.
        /// </summary>
        /// <typeparam name="TResult"> The type of results returned by the query. </typeparam>
        /// <param name="queryModel"> The query. </param>
        protected virtual void TrackEntitiesInResults<TResult>([NotNull] QueryModel queryModel)
        {
            Check.NotNull(queryModel, nameof(queryModel));

            if (!TrackResults(queryModel))
            {
                return;
            }

            var outputExpression 
                = new IncludeRemovingExpressionVisitor()
                    .Visit(queryModel.SelectClause.Selector);

            var resultItemType = _expression.Type.GetSequenceType();
            var isGrouping = resultItemType.IsGrouping();

            if (isGrouping)
            {
                var groupResultOperator
                    = queryModel.ResultOperators.OfType<GroupResultOperator>().LastOrDefault();

                if (groupResultOperator != null)
                {
                    outputExpression = groupResultOperator.ElementSelector;
                }
                else
                {
                    var subqueryExpression = ((queryModel.SelectClause.Selector as QuerySourceReferenceExpression)
                        ?.ReferencedQuerySource as MainFromClause)?.FromExpression as SubQueryExpression;

                    var nestedGroupResultOperator
                        = subqueryExpression?.QueryModel?.ResultOperators
                            ?.OfType<GroupResultOperator>()
                            .LastOrDefault();

                    if (nestedGroupResultOperator != null)
                    {
                        outputExpression = nestedGroupResultOperator.ElementSelector;
                    }
                }
            }

            var entityTrackingInfos
                = _entityResultFindingExpressionVisitorFactory
                    .Create(QueryCompilationContext)
                    .FindEntitiesInResult(outputExpression);

            if (entityTrackingInfos.Any())
            {
                MethodInfo trackingMethod;

                if (isGrouping)

                {
                    trackingMethod
                        = LinqOperatorProvider.TrackGroupedEntities
                            .MakeGenericMethod(
                                resultItemType.GenericTypeArguments[0],
                                resultItemType.GenericTypeArguments[1]);
                }
                else
                {
                    trackingMethod
                        = LinqOperatorProvider.TrackEntities
                            .MakeGenericMethod(
                                resultItemType,
                                outputExpression.Type);
                }

                _expression
                    = Expression.Call(
                        trackingMethod,
                        _expression,
                        QueryContextParameter,
                        Expression.Constant(entityTrackingInfos),
                        Expression.Constant(
                            _getEntityAccessors
                                .MakeGenericMethod(outputExpression.Type)
                                .Invoke(
                                    null,
                                    new object[]
                                    {
                                        entityTrackingInfos,
                                        outputExpression
                                    })));
            }
        }

        private class IncludeRemovingExpressionVisitor : RelinqExpressionVisitor
        {
            protected override Expression VisitMethodCall(MethodCallExpression node)
            {
                if (node.Method.IsGenericMethod
                    && node.Method.GetGenericMethodDefinition() == IncludeCompiler.IncludeMethodInfo)
                {
                    return node.Arguments[0];
                }

                return base.VisitMethodCall(node);
            }
        }

        private bool TrackResults(QueryModel queryModel)
        {
            // TODO: Unify with QCC

            var lastTrackingModifier
                = QueryCompilationContext.QueryAnnotations
                    .OfType<TrackingResultOperator>()
                    .LastOrDefault();

            return !(queryModel.GetOutputDataInfo() is StreamedScalarValueInfo)
                   && (QueryCompilationContext.TrackQueryResults || lastTrackingModifier != null)
                   && (lastTrackingModifier == null
                       || lastTrackingModifier.IsTracking);
        }

        private static readonly MethodInfo _getEntityAccessors
            = typeof(EntityQueryModelVisitor)
                .GetTypeInfo().GetDeclaredMethod(nameof(GetEntityAccessors));
        
        [UsedImplicitly]
        private static ICollection<Func<TResult, object>> GetEntityAccessors<TResult>(
            IEnumerable<EntityTrackingInfo> entityTrackingInfos,
            Expression selector)
            => (from entityTrackingInfo in entityTrackingInfos
                select
                (Func<TResult, object>)
                AccessorFindingExpressionVisitor
                    .FindAccessorLambda(
                        entityTrackingInfo.QuerySourceReferenceExpression,
                        selector,
                        Expression.Parameter(typeof(TResult), "result"))
                    .Compile())
                .ToList();

        /// <summary>
        ///     Creates an action to execute this query.
        /// </summary>
        /// <typeparam name="TResults"> The type of results that the query returns. </typeparam>
        /// <returns> An action that returns the results of the query. </returns>
        /// >
        protected virtual Func<QueryContext, TResults> CreateExecutorLambda<TResults>()
        {
            var queryExecutorExpression
                = Expression
                    .Lambda<Func<QueryContext, TResults>>(
                        _expression, QueryContextParameter);

            var queryExecutor = queryExecutorExpression.Compile();

            QueryCompilationContext.Logger.LogDebug(
                CoreEventId.QueryPlan,
                () =>
                    {
                        var queryPlan = _expressionPrinter.Print(queryExecutorExpression);

                        return queryPlan;
                    });

            return queryExecutor;
        }

        /// <summary>
        ///     Visits the root <see cref="QueryModel" /> node.
        /// </summary>
        /// <param name="queryModel"> The query. </param>
        public override void VisitQueryModel([NotNull] QueryModel queryModel)
        {
            Check.NotNull(queryModel, nameof(queryModel));

            base.VisitQueryModel(queryModel);

            if (_blockTaskExpressions)
            {
                _expression = _taskBlockingExpressionVisitor.Visit(_expression);
            }
        }

        /// <summary>
        ///     Visits the <see cref="MainFromClause" /> node.
        /// </summary>
        /// <param name="fromClause"> The node being visited. </param>
        /// <param name="queryModel"> The query. </param>
        public override void VisitMainFromClause(
            [NotNull] MainFromClause fromClause, [NotNull] QueryModel queryModel)
        {
            Check.NotNull(fromClause, nameof(fromClause));
            Check.NotNull(queryModel, nameof(queryModel));

            _expression = CompileMainFromClauseExpression(fromClause, queryModel);

            if (LinqOperatorProvider is AsyncLinqOperatorProvider
                && _expression.Type.TryGetElementType(typeof(IEnumerable<>)) != null)
            {
                LinqOperatorProvider = new LinqOperatorProvider();
            }

            CurrentParameter
                = Expression.Parameter(
                    _expression.Type.GetSequenceType(),
                    fromClause.ItemName);

            AddOrUpdateMapping(fromClause, CurrentParameter);
        }

        /// <summary>
        ///     Compiles the <see cref="MainFromClause" /> node.
        /// </summary>
        /// <param name="mainFromClause"> The node being compiled. </param>
        /// <param name="queryModel"> The query. </param>
        /// <returns> The compiled result. </returns>
        protected virtual Expression CompileMainFromClauseExpression(
            [NotNull] MainFromClause mainFromClause, [NotNull] QueryModel queryModel)
        {
            Check.NotNull(mainFromClause, nameof(mainFromClause));
            Check.NotNull(queryModel, nameof(queryModel));

            return ReplaceClauseReferences(mainFromClause.FromExpression, mainFromClause);
        }

        /// <summary>
        ///     Visits <see cref="AdditionalFromClause" /> nodes.
        /// </summary>
        /// <param name="fromClause"> The node being visited. </param>
        /// <param name="queryModel"> The query. </param>
        /// <param name="index"> Index of the node being visited. </param>
        public override void VisitAdditionalFromClause(
            [NotNull] AdditionalFromClause fromClause, [NotNull] QueryModel queryModel, int index)
        {
            Check.NotNull(fromClause, nameof(fromClause));
            Check.NotNull(queryModel, nameof(queryModel));

            var fromExpression
                = CompileAdditionalFromClauseExpression(fromClause, queryModel);
            
            var innerItemParameter
                = Expression.Parameter(
                    fromExpression.Type.GetSequenceType(), fromClause.ItemName);

            var transparentIdentifierType
                = typeof(TransparentIdentifier<,>)
                    .MakeGenericType(CurrentParameter.Type, innerItemParameter.Type);

            _expression
                = Expression.Call(
                    LinqOperatorProvider.SelectMany
                        .MakeGenericMethod(
                            CurrentParameter.Type,
                            innerItemParameter.Type,
                            transparentIdentifierType),
                    _expression,
                    Expression.Lambda(fromExpression, CurrentParameter),
                    Expression.Lambda(
                        CallCreateTransparentIdentifier(
                            transparentIdentifierType, CurrentParameter, innerItemParameter),
                        CurrentParameter,
                        innerItemParameter));

            IntroduceTransparentScope(fromClause, queryModel, index, transparentIdentifierType);
        }

        /// <summary>
        ///     Compiles <see cref="AdditionalFromClause" /> nodes.
        /// </summary>
        /// <param name="additionalFromClause"> The node being compiled. </param>
        /// <param name="queryModel"> The query. </param>
        /// <returns> The compiled result. </returns>
        protected virtual Expression CompileAdditionalFromClauseExpression(
            [NotNull] AdditionalFromClause additionalFromClause, [NotNull] QueryModel queryModel)
        {
            Check.NotNull(additionalFromClause, nameof(additionalFromClause));
            Check.NotNull(queryModel, nameof(queryModel));

            return ReplaceClauseReferences(additionalFromClause.FromExpression, additionalFromClause);
        }

        /// <summary>
        ///     Visits <see cref="JoinClause" /> nodes.
        /// </summary>
        /// <param name="joinClause"> The node being visited. </param>
        /// <param name="queryModel"> The query. </param>
        /// <param name="index"> Index of the node being visited. </param>
        public override void VisitJoinClause(
            [NotNull] JoinClause joinClause, [NotNull] QueryModel queryModel, int index)
        {
            Check.NotNull(joinClause, nameof(joinClause));
            Check.NotNull(queryModel, nameof(queryModel));
            
            var outerKeySelectorExpression
                = CompileJoinClauseOuterKeySelectorExpression(
                    joinClause,
                    joinClause.OuterKeySelector,
                    queryModel);
            
            var innerSequenceExpression
                = CompileJoinClauseInnerSequenceExpression(
                    joinClause,
                    joinClause.InnerSequence,
                    queryModel);

            var innerItemParameter
                = Expression.Parameter(
                    innerSequenceExpression.Type.GetSequenceType(), joinClause.ItemName);

            AddOrUpdateMapping(joinClause, innerItemParameter);

            var innerKeySelectorExpression
                = CompileJoinClauseInnerKeySelectorExpression(
                    joinClause,
                    joinClause.InnerKeySelector,
                    innerItemParameter,
                    queryModel);

            var transparentIdentifierType
                = typeof(TransparentIdentifier<,>)
                    .MakeGenericType(CurrentParameter.Type, innerItemParameter.Type);

            _expression
                = Expression.Call(
                    LinqOperatorProvider.Join
                        .MakeGenericMethod(
                            CurrentParameter.Type,
                            innerItemParameter.Type,
                            outerKeySelectorExpression.Type,
                            transparentIdentifierType),
                    _expression,
                    innerSequenceExpression,
                    Expression.Lambda(outerKeySelectorExpression, CurrentParameter),
                    Expression.Lambda(innerKeySelectorExpression, innerItemParameter),
                    Expression.Lambda(
                        CallCreateTransparentIdentifier(
                            transparentIdentifierType,
                            CurrentParameter,
                            innerItemParameter),
                        CurrentParameter,
                        innerItemParameter));

            IntroduceTransparentScope(joinClause, queryModel, index, transparentIdentifierType);
        }

        /// <summary>
        ///     Visits <see cref="GroupJoinClause" /> nodes
        /// </summary>
        /// <param name="groupJoinClause"> The node being visited. </param>
        /// <param name="queryModel"> The query. </param>
        /// <param name="index"> Index of the node being visited. </param>
        public override void VisitGroupJoinClause(
            [NotNull] GroupJoinClause groupJoinClause, [NotNull] QueryModel queryModel, int index)
        {
            Check.NotNull(groupJoinClause, nameof(groupJoinClause));
            Check.NotNull(queryModel, nameof(queryModel));

            var outerKeySelectorExpression
                = CompileJoinClauseOuterKeySelectorExpression(
                    groupJoinClause,
                    groupJoinClause.JoinClause.OuterKeySelector,
                    queryModel);

            var innerSequenceExpression
                = CompileJoinClauseInnerSequenceExpression(
                    groupJoinClause, 
                    groupJoinClause.JoinClause.InnerSequence,
                    queryModel);

            var innerItemParameter
                = Expression.Parameter(
                    innerSequenceExpression.Type.GetSequenceType(),
                    groupJoinClause.JoinClause.ItemName);

            AddOrUpdateMapping(groupJoinClause.JoinClause, innerItemParameter);

            var innerKeySelectorExpression
                = CompileJoinClauseInnerKeySelectorExpression(
                    groupJoinClause,
                    groupJoinClause.JoinClause.InnerKeySelector,
                    innerItemParameter,
                    queryModel);

            var innerItemsParameter
                = Expression.Parameter(
                    LinqOperatorProvider.MakeSequenceType(innerItemParameter.Type),
                    groupJoinClause.ItemName);

            var transparentIdentifierType
                = typeof(TransparentIdentifier<,>)
                    .MakeGenericType(CurrentParameter.Type, innerItemsParameter.Type);

            _expression
                = Expression.Call(
                    LinqOperatorProvider.GroupJoin
                        .MakeGenericMethod(
                            CurrentParameter.Type,
                            innerItemParameter.Type,
                            innerKeySelectorExpression.Type,
                            transparentIdentifierType),
                    _expression,
                    innerSequenceExpression,
                    Expression.Lambda(outerKeySelectorExpression, CurrentParameter),
                    Expression.Lambda(innerKeySelectorExpression, innerItemParameter),
                    Expression.Lambda(
                        CallCreateTransparentIdentifier(
                            transparentIdentifierType,
                            CurrentParameter,
                            innerItemsParameter),
                        CurrentParameter,
                        innerItemsParameter));

            IntroduceTransparentScope(groupJoinClause, queryModel, index, transparentIdentifierType);
        }

        /// <summary>
        ///     Compiles the outer key selector expression for <see cref="JoinClause" /> 
        ///     and <see cref="GroupJoinClause" /> nodes.
        /// </summary>
        /// <param name="querySource"> The node being compiled. </param>
        /// <param name="outerKeySelector"> The outer key selector being compiled. </param>
        /// <param name="queryModel"> The query. </param>
        /// <returns> The compiled result. </returns>
        protected virtual Expression CompileJoinClauseOuterKeySelectorExpression(
            [NotNull] IQuerySource querySource,
            [NotNull] Expression outerKeySelector,
            [NotNull] QueryModel queryModel)
        {
            Check.NotNull(querySource, nameof(querySource));
            Check.NotNull(outerKeySelector, nameof(outerKeySelector));
            Check.NotNull(queryModel, nameof(queryModel));

            return ReplaceClauseReferences(outerKeySelector, querySource);
        }

        /// <summary>
        ///     Compiles the inner sequence expression for <see cref="JoinClause" /> 
        ///     and <see cref="GroupJoinClause" /> nodes.
        /// </summary>
        /// <param name="querySource"> The node being compiled. </param>
        /// <param name="innerSequence"> The inner sequence being compiled. </param>
        /// <param name="queryModel"> The query. </param>
        /// <returns> The compiled result. </returns>
        protected virtual Expression CompileJoinClauseInnerSequenceExpression(
            [NotNull] IQuerySource querySource,
            [NotNull] Expression innerSequence,
            [NotNull] QueryModel queryModel)
        {
            Check.NotNull(querySource, nameof(querySource));
            Check.NotNull(innerSequence, nameof(innerSequence));
            Check.NotNull(queryModel, nameof(queryModel));

            return ReplaceClauseReferences(innerSequence, querySource);
        }

        /// <summary>
        ///     Compiles the inner key selector expression for <see cref="JoinClause" /> 
        ///     and <see cref="GroupJoinClause" /> nodes.
        /// </summary>
        /// <param name="querySource"> The node being compiled. </param>
        /// <param name="innerKeySelector"> The inner key selector being compiled. </param>
        /// <param name="parameter"> The parameter that will be passed to the inner key selector. </param>
        /// <param name="queryModel"> The query. </param>
        /// <returns> The compiled result. </returns>
        protected virtual Expression CompileJoinClauseInnerKeySelectorExpression(
            [NotNull] IQuerySource querySource,
            [NotNull] Expression innerKeySelector,
            [NotNull] ParameterExpression parameter,
            [NotNull] QueryModel queryModel)
        {
            Check.NotNull(querySource, nameof(querySource));
            Check.NotNull(innerKeySelector, nameof(innerKeySelector));
            Check.NotNull(parameter, nameof(parameter));
            Check.NotNull(queryModel, nameof(queryModel));

            return ReplaceClauseReferences(innerKeySelector, querySource);
        }

        /// <summary>
        ///     Visits <see cref="WhereClause" /> nodes.
        /// </summary>
        /// <param name="whereClause"> The node being visited. </param>
        /// <param name="queryModel"> The query. </param>
        /// <param name="index"> Index of the node being visited. </param>
        public override void VisitWhereClause(
            [NotNull] WhereClause whereClause, [NotNull] QueryModel queryModel, int index)
        {
            Check.NotNull(whereClause, nameof(whereClause));
            Check.NotNull(queryModel, nameof(queryModel));

            var predicate = ReplaceClauseReferences(whereClause.Predicate);

            _expression
                = Expression.Call(
                    LinqOperatorProvider.Where.MakeGenericMethod(CurrentParameter.Type),
                    _expression,
                    Expression.Lambda(predicate, CurrentParameter));
        }

        /// <summary>
        ///     Visits <see cref="Ordering" /> nodes.
        /// </summary>
        /// <param name="ordering"> The node being visited. </param>
        /// <param name="queryModel"> The query. </param>
        /// <param name="orderByClause"> The <see cref="OrderByClause" /> for the ordering. </param>
        /// <param name="index"> Index of the node being visited. </param>
        public override void VisitOrdering(
            [NotNull] Ordering ordering,
            [NotNull] QueryModel queryModel,
            [NotNull] OrderByClause orderByClause,
            int index)
        {
            Check.NotNull(ordering, nameof(ordering));
            Check.NotNull(queryModel, nameof(queryModel));
            Check.NotNull(orderByClause, nameof(orderByClause));

            var expression = ReplaceClauseReferences(ordering.Expression);

            _expression
                = Expression.Call(
                    (index == 0
                            ? LinqOperatorProvider.OrderBy
                            : LinqOperatorProvider.ThenBy)
                        .MakeGenericMethod(CurrentParameter.Type, expression.Type),
                    _expression,
                    Expression.Lambda(expression, CurrentParameter),
                    Expression.Constant(ordering.OrderingDirection));
        }

        /// <summary>
        ///     Visits <see cref="SelectClause" /> nodes.
        /// </summary>
        /// <param name="selectClause"> The node being visited. </param>
        /// <param name="queryModel"> The query. </param>
        public override void VisitSelectClause(
            [NotNull] SelectClause selectClause, [NotNull] QueryModel queryModel)
        {
            Check.NotNull(selectClause, nameof(selectClause));
            Check.NotNull(queryModel, nameof(queryModel));

            var sequenceType = _expression.Type.GetSequenceType();

            if (selectClause.Selector.Type == sequenceType
                && selectClause.Selector is QuerySourceReferenceExpression)
            {
                return;
            }

            var selector
                = ReplaceClauseReferences(
                    _projectionExpressionVisitorFactory
                        .Create(this, queryModel.MainFromClause)
                        .Visit(selectClause.Selector),
                    inProjection: true);

            if ((selector.Type != sequenceType
                 || !(selectClause.Selector is QuerySourceReferenceExpression))
                && !queryModel.ResultOperators
                    .Select(ro => ro.GetType())
                    .Any(t =>
                        t == typeof(GroupResultOperator)
                        || t == typeof(AllResultOperator)))
            {
                _expression
                    = Expression.Call(
                        LinqOperatorProvider.Select
                            .MakeGenericMethod(CurrentParameter.Type, selector.Type),
                        _expression,
                        Expression.Lambda(selector, CurrentParameter));
            }
        }

        /// <summary>
        ///     Visits <see cref="ResultOperatorBase" /> nodes.
        /// </summary>
        /// <param name="resultOperator"> The node being visited. </param>
        /// <param name="queryModel"> The query. </param>
        /// <param name="index"> Index of the node being visited. </param>
        public override void VisitResultOperator(
            [NotNull] ResultOperatorBase resultOperator, [NotNull] QueryModel queryModel, int index)
        {
            Check.NotNull(resultOperator, nameof(resultOperator));
            Check.NotNull(queryModel, nameof(queryModel));

            _expression
                = _resultOperatorHandler
                    .HandleResultOperator(this, resultOperator, queryModel);
        }

        #region Transparent Identifiers

        /// <summary>
        ///     This API supports the Entity Framework Core infrastructure and is not intended to be used
        ///     directly from your code. This API may change or be removed in future releases.
        /// </summary>
        protected virtual Type CreateTransparentIdentifierType([NotNull] Type outerType, [NotNull] Type innerType)
            => typeof(TransparentIdentifier<,>)
                .MakeGenericType(
                    Check.NotNull(outerType, nameof(outerType)), 
                    Check.NotNull(innerType, nameof(innerType)));

        private const string CreateTransparentIdentifierMethodName = "CreateTransparentIdentifier";

        private struct TransparentIdentifier<TOuter, TInner>
        {
            [UsedImplicitly]
            public static TransparentIdentifier<TOuter, TInner> CreateTransparentIdentifier(TOuter outer, TInner inner)
                => new TransparentIdentifier<TOuter, TInner>(outer, inner);

            private TransparentIdentifier(TOuter outer, TInner inner)
            {
                Outer = outer;
                Inner = inner;
            }

            [UsedImplicitly]
            public TOuter Outer;

            [UsedImplicitly]
            public TInner Inner;
        }

        /// <summary>
        ///     This API supports the Entity Framework Core infrastructure and is not intended to be used
        ///     directly from your code. This API may change or be removed in future releases.
        /// </summary>
        protected virtual Expression CallCreateTransparentIdentifier(
            [NotNull] Type transparentIdentifierType,
            [NotNull] Expression outerExpression,
            [NotNull] Expression innerExpression)
        {
            Check.NotNull(transparentIdentifierType, nameof(transparentIdentifierType));
            Check.NotNull(outerExpression, nameof(outerExpression));
            Check.NotNull(innerExpression, nameof(innerExpression));

            var createTransparentIdentifierMethodInfo
                = transparentIdentifierType.GetTypeInfo().GetDeclaredMethod(CreateTransparentIdentifierMethodName);

            return Expression.Call(createTransparentIdentifierMethodInfo, outerExpression, innerExpression);
        }

        private static Expression AccessOuterTransparentField(
            Type transparentIdentifierType, Expression targetExpression)
        {
            var fieldInfo = transparentIdentifierType.GetTypeInfo().GetDeclaredField("Outer");

            return Expression.Field(targetExpression, fieldInfo);
        }
        
        private static Expression AccessInnerTransparentField(
            Type transparentIdentifierType, Expression targetExpression)
        {
            var fieldInfo = transparentIdentifierType.GetTypeInfo().GetDeclaredField("Inner");

            return Expression.Field(targetExpression, fieldInfo);
        }

        /// <summary>
        ///     This API supports the Entity Framework Core infrastructure and is not intended to be used
        ///     directly from your code. This API may change or be removed in future releases.
        /// </summary>
        protected virtual void IntroduceTransparentScope(
            [NotNull] IQuerySource querySource,
            [NotNull] QueryModel queryModel,
            int index,
            [NotNull] Type transparentIdentifierType)
        {
            Check.NotNull(querySource, nameof(querySource));
            Check.NotNull(queryModel, nameof(queryModel));
            Check.NotNull(transparentIdentifierType, nameof(transparentIdentifierType));

            CurrentParameter
                = Expression.Parameter(
                    transparentIdentifierType,
                    string.Format(CultureInfo.InvariantCulture, "t{0}", _transparentParameterCounter++));

            var outerAccessExpression
                = AccessOuterTransparentField(transparentIdentifierType, CurrentParameter);

            RescopeTransparentAccess(queryModel.MainFromClause, outerAccessExpression);

            for (var i = 0; i < index; i++)
            {
                var bodyClause = queryModel.BodyClauses[i] as IQuerySource;

                if (bodyClause != null)
                {
                    RescopeTransparentAccess(bodyClause, outerAccessExpression);

                    var groupJoinClause = bodyClause as GroupJoinClause;

                    if (groupJoinClause != null
                        && QueryCompilationContext.QuerySourceMapping
                            .ContainsMapping(groupJoinClause.JoinClause))
                    {
                        RescopeTransparentAccess(groupJoinClause.JoinClause, outerAccessExpression);
                    }
                }
            }

            AddOrUpdateMapping(querySource, AccessInnerTransparentField(transparentIdentifierType, CurrentParameter));
        }

        private void RescopeTransparentAccess(IQuerySource querySource, Expression targetExpression)
        {
            var memberAccessExpression
                = ShiftMemberAccess(
                    targetExpression,
                    _queryCompilationContext.QuerySourceMapping.GetExpression(querySource));

            _queryCompilationContext.QuerySourceMapping.ReplaceMapping(querySource, memberAccessExpression);
        }

        private static Expression ShiftMemberAccess(Expression targetExpression, Expression currentExpression)
        {
            var memberExpression = currentExpression as MemberExpression;

            if (memberExpression == null)
            {
                return targetExpression;
            }

            try
            {
                return Expression.MakeMemberAccess(
                    ShiftMemberAccess(targetExpression, memberExpression.Expression),
                    memberExpression.Member);
            }
            catch (ArgumentException)
            {
                // Member is not defined on the new target expression.
                // This is due to stale QuerySourceMappings, which we can't
                // remove due to there not being an API on QuerySourceMapping.
            }

            return currentExpression;
        }

        #endregion

        /// <summary>
        ///     Translates a re-linq query model expression into a compiled query expression.
        /// </summary>
        /// <param name="expression"> The re-linq query model expression. </param>
        /// <param name="querySource"> The query source. </param>
        /// <param name="inProjection"> True when the expression is a projector. </param>
        /// <returns>
        ///     A compiled query expression fragment.
        /// </returns>
        public virtual Expression ReplaceClauseReferences(
            [NotNull] Expression expression,
            [CanBeNull] IQuerySource querySource = null,
            bool inProjection = false)
        {
            Check.NotNull(expression, nameof(expression));

            expression
                = _entityQueryableExpressionVisitorFactory
                    .Create(this, querySource)
                    .Visit(expression);

            expression
                = _memberAccessBindingExpressionVisitorFactory
                    .Create(QueryCompilationContext.QuerySourceMapping, this)
                    .Visit(expression);

            if (!inProjection
                && expression.Type != typeof(string)
                && expression.Type != typeof(byte[])
                && _expression?.Type.TryGetElementType(typeof(IAsyncEnumerable<>)) != null)
            {
                var elementType = expression.Type.TryGetElementType(typeof(IEnumerable<>));

                if (elementType != null)
                {
                    var asyncLinqOperatorProvider = LinqOperatorProvider as AsyncLinqOperatorProvider;
                    if (asyncLinqOperatorProvider != null)
                    {
                        return
                            Expression.Call(
                                asyncLinqOperatorProvider
                                    .ToAsyncEnumerable
                                    .MakeGenericMethod(elementType),
                                expression);
                    }
                }
            }

            return expression;
        }

        /// <summary>
        ///     Adds or updates the expression mapped to a query source.
        /// </summary>
        /// <param name="querySource"> The query source. </param>
        /// <param name="expression"> The expression mapped to the query source. </param>
        public virtual void AddOrUpdateMapping(
            [NotNull] IQuerySource querySource, [NotNull] Expression expression)
        {
            Check.NotNull(querySource, nameof(querySource));
            Check.NotNull(expression, nameof(expression));

            QueryCompilationContext.AddOrUpdateMapping(querySource, expression);
        }

        #region Binding

        /// <summary>
        ///     Binds a value buffer read.
        /// </summary>
        /// <param name="memberType"> Type of the member. </param>
        /// <param name="expression"> The target expression. </param>
        /// <param name="index"> A value buffer index. </param>
        /// <returns>
        ///     A value buffer read expression.
        /// </returns>
        public virtual Expression BindReadValueMethod(
            [NotNull] Type memberType,
            [NotNull] Expression expression,
            int index)
        {
            Check.NotNull(memberType, nameof(memberType));
            Check.NotNull(expression, nameof(expression));

            return _entityMaterializerSource
                .CreateReadValueExpression(expression, memberType, index);
        }

        /// <summary>
        ///     Binds a value buffer read.
        /// </summary>
        /// <param name="valueBufferRead"> The value buffer read expression. </param>
        /// <param name="index"> A value buffer index. </param>
        /// <returns>
        ///     A value buffer read expression.
        /// </returns>
        public virtual Expression BindValueBufferReadExpression(
            [NotNull] ValueBufferReadExpression valueBufferRead,
            int index)
        {
            Check.NotNull(valueBufferRead, nameof(valueBufferRead));

            return _entityMaterializerSource
                .CreateReadValueExpression(
                    valueBufferRead.ValueBuffer, 
                    valueBufferRead.Type, 
                    index);
        }

        /// <summary>
        ///     Binds a navigation path property expression.
        /// </summary>
        /// <typeparam name="TResult"> Type of the result. </typeparam>
        /// <param name="propertyExpression"> The property expression. </param>
        /// <param name="propertyBinder"> The property binder. </param>
        /// <returns>
        ///     A TResult.
        /// </returns>
        public virtual TResult BindNavigationPathPropertyExpression<TResult>(
            [NotNull] Expression propertyExpression,
            [NotNull] Func<IEnumerable<IPropertyBase>, IQuerySource, TResult> propertyBinder)
        {
            Check.NotNull(propertyExpression, nameof(propertyExpression));
            Check.NotNull(propertyBinder, nameof(propertyBinder));

            return BindExpressionCore(propertyExpression, propertyBinder);
        }

        /// <summary>
        ///     Binds a member expression.
        /// </summary>
        /// <typeparam name="TResult"> Type of the result. </typeparam>
        /// <param name="memberExpression"> The member access expression. </param>
        /// <param name="memberBinder"> The member binder. </param>
        /// <returns>
        ///     A TResult.
        /// </returns>
        public virtual TResult BindMemberExpression<TResult>(
            [NotNull] MemberExpression memberExpression,
            [NotNull] Func<IProperty, IQuerySource, TResult> memberBinder)
        {
            Check.NotNull(memberExpression, nameof(memberExpression));
            Check.NotNull(memberBinder, nameof(memberBinder));

            return BindExpressionCore(memberExpression, (properties, querySource) =>
            {
                var property = properties.Count == 1 ? properties[0] as IProperty : null;

                return property != null
                    ? memberBinder(property, querySource)
                    : default(TResult);
            });
        }

        /// <summary>
        ///     Binds a member expression.
        /// </summary>
        /// <param name="memberExpression"> The member access expression. </param>
        /// <param name="memberBinder"> The member binder. </param>
        public virtual void BindMemberExpression(
            [NotNull] MemberExpression memberExpression,
            [NotNull] Action<IProperty, IQuerySource> memberBinder)
        {
            Check.NotNull(memberExpression, nameof(memberExpression));
            Check.NotNull(memberBinder, nameof(memberBinder));

            BindMemberExpression(memberExpression, (property, querySource) =>
            {
                memberBinder(property, querySource);

                return default(object);
            });
        }

        /// <summary>
        ///     Binds a method call expression.
        /// </summary>
        /// <typeparam name="TResult"> Type of the result. </typeparam>
        /// <param name="methodCallExpression"> The method call expression. </param>
        /// <param name="methodCallBinder"> The method call binder. </param>
        /// <returns>
        ///     A TResult.
        /// </returns>
        public virtual TResult BindMethodCallExpression<TResult>(
            [NotNull] MethodCallExpression methodCallExpression,
            [NotNull] Func<IProperty, IQuerySource, TResult> methodCallBinder)
        {
            Check.NotNull(methodCallExpression, nameof(methodCallExpression));
            Check.NotNull(methodCallBinder, nameof(methodCallBinder));

            return BindExpressionCore(methodCallExpression, (properties, querySource) =>
            {
                var property = properties.Count == 1 ? properties[0] as IProperty : null;

                return property != null ? methodCallBinder(property, querySource) : default(TResult);
            });
        }

        /// <summary>
        ///     Binds a method call expression.
        /// </summary>
        /// <param name="methodCallExpression"> The method call expression. </param>
        /// <param name="methodCallBinder"> The method call binder. </param>
        public virtual void BindMethodCallExpression(
            [NotNull] MethodCallExpression methodCallExpression,
            [NotNull] Action<IProperty, IQuerySource> methodCallBinder)
        {
            Check.NotNull(methodCallExpression, nameof(methodCallExpression));
            Check.NotNull(methodCallBinder, nameof(methodCallBinder));

            BindMethodCallExpression(methodCallExpression, (property, querySource) =>
            {
                methodCallBinder(property, querySource);

                return default(object);
            });
        }

        private TResult BindExpressionCore<TResult>(
            Expression expression,
            Func<IReadOnlyList<IPropertyBase>, IQuerySource, TResult> propertyBinder)
        {
            QuerySourceReferenceExpression querySourceReferenceExpression = null;
            var properties = new List<IPropertyBase>();
            var memberExpression = expression as MemberExpression;
            var methodCallExpression = expression as MethodCallExpression;

            while (memberExpression?.Expression != null
                   || IsPropertyMethod(methodCallExpression?.Method)
                   && methodCallExpression?.Arguments[0] != null)
            {
                var propertyName = memberExpression?.Member.Name
                                   ?? (string)(methodCallExpression.Arguments[1] as ConstantExpression)?.Value;

                expression = memberExpression?.Expression ?? methodCallExpression.Arguments[0];

                // in case of inheritance there might be convert to derived type here, so we want to check it first
                var entityType = QueryCompilationContext.Model.FindEntityType(expression.Type);

                expression = expression.RemoveConvert();

                if (entityType == null)
                {
                    entityType = QueryCompilationContext.Model.FindEntityType(expression.Type);

                    if (entityType == null)
                    {
                        break;
                    }
                }

                var property
                    = (IPropertyBase)entityType.FindProperty(propertyName)
                      ?? entityType.FindNavigation(propertyName);

                if (property == null)
                {
                    if (IsPropertyMethod(methodCallExpression?.Method))
                    {
                        throw new InvalidOperationException(
                            CoreStrings.PropertyNotFound(propertyName, entityType.DisplayName()));
                    }

                    break;
                }

                properties.Add(property);

                querySourceReferenceExpression = expression as QuerySourceReferenceExpression;
                memberExpression = expression as MemberExpression;
                methodCallExpression = expression as MethodCallExpression;
            }

            return propertyBinder(
                properties.AsEnumerable().Reverse().ToList(),
                querySourceReferenceExpression?.ReferencedQuerySource);
        }

        #endregion
    }
}
