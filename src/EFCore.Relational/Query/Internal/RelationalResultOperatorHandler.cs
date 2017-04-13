// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore.Extensions.Internal;
using Microsoft.EntityFrameworkCore.Internal;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Query.Expressions;
using Microsoft.EntityFrameworkCore.Query.ExpressionVisitors;
using Microsoft.EntityFrameworkCore.Query.ExpressionVisitors.Internal;
using Remotion.Linq;
using Remotion.Linq.Clauses;
using Remotion.Linq.Clauses.ResultOperators;

namespace Microsoft.EntityFrameworkCore.Query.Internal
{
    /// <summary>
    ///     This API supports the Entity Framework Core infrastructure and is not intended to be used
    ///     directly from your code. This API may change or be removed in future releases.
    /// </summary>
    public class RelationalResultOperatorHandler : IRelationalResultOperatorHandler
    {
        private sealed class HandlerContext
        {
            private readonly IResultOperatorHandler _resultOperatorHandler;
            private readonly ISqlTranslatingExpressionVisitorFactory _sqlTranslatingExpressionVisitorFactory;

            public HandlerContext(
                IResultOperatorHandler resultOperatorHandler,
                IModel model,
                ISqlTranslatingExpressionVisitorFactory sqlTranslatingExpressionVisitorFactory,
                ISelectExpressionFactory selectExpressionFactory,
                RelationalQueryModelVisitor queryModelVisitor,
                ResultOperatorBase resultOperator,
                QueryModel queryModel,
                SelectExpression selectExpression)
            {
                _resultOperatorHandler = resultOperatorHandler;
                _sqlTranslatingExpressionVisitorFactory = sqlTranslatingExpressionVisitorFactory;

                Model = model;
                SelectExpressionFactory = selectExpressionFactory;
                QueryModelVisitor = queryModelVisitor;
                ResultOperator = resultOperator;
                QueryModel = queryModel;
                SelectExpression = selectExpression;
            }

            public IModel Model { get; }
            public ISelectExpressionFactory SelectExpressionFactory { get; }
            public ResultOperatorBase ResultOperator { get; }
            public SelectExpression SelectExpression { get; }
            public QueryModel QueryModel { get; }
            public RelationalQueryModelVisitor QueryModelVisitor { get; }
            public Expression EvalOnServer => QueryModelVisitor.Expression;

            public Expression EvalOnClient(bool requiresClientResultOperator = true)
            {
                QueryModelVisitor.RequiresClientResultOperator = requiresClientResultOperator;

                return _resultOperatorHandler
                    .HandleResultOperator(QueryModelVisitor, ResultOperator, QueryModel);
            }

            public SqlTranslatingExpressionVisitor CreateSqlTranslatingVisitor()
                => _sqlTranslatingExpressionVisitorFactory.Create(QueryModelVisitor, SelectExpression);
        }

        private static readonly Dictionary<Type, Func<HandlerContext, Expression>>
            _resultHandlers = new Dictionary<Type, Func<HandlerContext, Expression>>
            {
                { typeof(AllResultOperator), HandleAll },
                { typeof(AnyResultOperator), HandleAny },
                { typeof(AverageResultOperator), HandleAverage },
                { typeof(CastResultOperator), HandleCast },
                { typeof(ContainsResultOperator), HandleContains },
                { typeof(CountResultOperator), HandleCount },
                { typeof(LongCountResultOperator), HandleLongCount },
                { typeof(DefaultIfEmptyResultOperator), HandleDefaultIfEmpty },
                { typeof(DistinctResultOperator), HandleDistinct },
                { typeof(FirstResultOperator), HandleFirst },
                { typeof(GroupResultOperator), HandleGroup },
                { typeof(LastResultOperator), HandleLast },
                { typeof(MaxResultOperator), HandleMax },
                { typeof(MinResultOperator), HandleMin },
                { typeof(SingleResultOperator), HandleSingle },
                { typeof(SkipResultOperator), HandleSkip },
                { typeof(SumResultOperator), HandleSum },
                { typeof(TakeResultOperator), HandleTake }
            };

        private readonly IModel _model;
        private readonly ISqlTranslatingExpressionVisitorFactory _sqlTranslatingExpressionVisitorFactory;
        private readonly ISelectExpressionFactory _selectExpressionFactory;
        private readonly IResultOperatorHandler _resultOperatorHandler;

        /// <summary>
        ///     This API supports the Entity Framework Core infrastructure and is not intended to be used
        ///     directly from your code. This API may change or be removed in future releases.
        /// </summary>
        public RelationalResultOperatorHandler(
            [NotNull] IModel model,
            [NotNull] ISqlTranslatingExpressionVisitorFactory sqlTranslatingExpressionVisitorFactory,
            [NotNull] ISelectExpressionFactory selectExpressionFactory,
            [NotNull] IResultOperatorHandler resultOperatorHandler)
        {
            _model = model;
            _sqlTranslatingExpressionVisitorFactory = sqlTranslatingExpressionVisitorFactory;
            _selectExpressionFactory = selectExpressionFactory;
            _resultOperatorHandler = resultOperatorHandler;
        }

        /// <summary>
        ///     This API supports the Entity Framework Core infrastructure and is not intended to be used
        ///     directly from your code. This API may change or be removed in future releases.
        /// </summary>
        public virtual Expression HandleResultOperator(
            EntityQueryModelVisitor entityQueryModelVisitor,
            ResultOperatorBase resultOperator,
            QueryModel queryModel)
        {
            var relationalQueryModelVisitor
                = (RelationalQueryModelVisitor)entityQueryModelVisitor;

            var selectExpression
                = relationalQueryModelVisitor
                    .TryGetQuery(queryModel.MainFromClause);

            var handlerContext
                = new HandlerContext(
                    _resultOperatorHandler,
                    _model,
                    _sqlTranslatingExpressionVisitorFactory,
                    _selectExpressionFactory,
                    relationalQueryModelVisitor,
                    resultOperator,
                    queryModel,
                    selectExpression);

            if (relationalQueryModelVisitor.RequiresClientEval
                || relationalQueryModelVisitor.RequiresClientSelectMany
                || relationalQueryModelVisitor.RequiresClientJoin
                || relationalQueryModelVisitor.RequiresClientFilter
                || relationalQueryModelVisitor.RequiresClientOrderBy
                || relationalQueryModelVisitor.RequiresClientResultOperator
                || !_resultHandlers.TryGetValue(resultOperator.GetType(), out var resultHandler)
                || selectExpression == null)
            {
                return handlerContext.EvalOnClient();
            }

            return resultHandler(handlerContext);
        }

        private static Expression HandleAll(HandlerContext handlerContext)
        {
            var sqlTranslatingVisitor
                = handlerContext.CreateSqlTranslatingVisitor();

            var predicate
                = sqlTranslatingVisitor.Visit(
                    ((AllResultOperator)handlerContext.ResultOperator).Predicate);

            if (predicate != null)
            {
                var innerSelectExpression = handlerContext.SelectExpression.Clone();

                innerSelectExpression.ClearProjection();
                innerSelectExpression.AddToProjection(Expression.Constant(1));
                innerSelectExpression.AddToPredicate(Expression.Not(predicate));

                if (innerSelectExpression.Limit == null
                    && innerSelectExpression.Offset == null)
                {
                    innerSelectExpression.ClearOrderBy();
                }

                SetConditionAsProjection(
                    handlerContext,
                    Expression.Not(new ExistsExpression(innerSelectExpression)));

                return TransformClientExpression(handlerContext, typeof(bool));
            }

            return handlerContext.EvalOnClient();
        }

        private static Expression HandleAny(HandlerContext handlerContext)
        {
            var innerSelectExpression = handlerContext.SelectExpression.Clone();

            innerSelectExpression.ClearProjection();
            innerSelectExpression.AddToProjection(Expression.Constant(1));

            if (innerSelectExpression.Limit == null
                && innerSelectExpression.Offset == null)
            {
                innerSelectExpression.ClearOrderBy();
            }

            SetConditionAsProjection(
                handlerContext,
                    new ExistsExpression(innerSelectExpression));

            return TransformClientExpression(handlerContext, typeof(bool));
        }

        private static Expression HandleAverage(HandlerContext handlerContext)
        {
            if (!handlerContext.QueryModelVisitor.RequiresClientProjection
                && handlerContext.SelectExpression.Projection.Count == 1)
            {
                var expression = handlerContext.SelectExpression.Projection.First();

                if (!(expression.RemoveConvert() is SelectExpression))
                {
                    var inputType = handlerContext.QueryModel.SelectClause.Selector.Type;
                    var outputType = inputType;

                    var nonNullableInputType = inputType.UnwrapNullableType();
                    if (nonNullableInputType == typeof(int)
                        || nonNullableInputType == typeof(long))
                    {
                        outputType = inputType.IsNullableType() ? typeof(double?) : typeof(double);
                    }

                    expression = new ExplicitCastExpression(expression, outputType);
                    var averageExpression = new SqlFunctionExpression("AVG", outputType, new [] { expression });

                    handlerContext.SelectExpression.SetProjectionExpression(averageExpression);

                    return TransformClientExpression(handlerContext, averageExpression.Type);
                }
            }

            return handlerContext.EvalOnClient();
        }

        private static Expression HandleCast(HandlerContext handlerContext)
            => handlerContext.EvalOnClient(requiresClientResultOperator: false);

        private static Expression HandleContains(HandlerContext handlerContext)
        {
            var filteringVisitor = handlerContext.CreateSqlTranslatingVisitor();

            var itemResultOperator = (ContainsResultOperator)handlerContext.ResultOperator;

            var item = filteringVisitor.Visit(itemResultOperator.Item);
            if (item != null)
            {
                if (item is SelectExpression itemSelectExpression)
                {
                    var entityType = handlerContext.Model.FindEntityType(handlerContext.QueryModel.MainFromClause.ItemType);

                    if (entityType != null)
                    {
                        var outerSelectExpression = handlerContext.SelectExpressionFactory.Create(handlerContext.QueryModelVisitor.QueryCompilationContext);

                        var collectionSelectExpression
                            = handlerContext.SelectExpression.Clone(handlerContext.QueryModelVisitor.QueryCompilationContext.CreateUniqueTableAlias());
                        outerSelectExpression.AddTable(collectionSelectExpression);

                        itemSelectExpression.Alias = handlerContext.QueryModelVisitor.QueryCompilationContext.CreateUniqueTableAlias();
                        var joinExpression = outerSelectExpression.AddInnerJoin(itemSelectExpression);

                        foreach (var property in entityType.FindPrimaryKey().Properties)
                        {
                            var itemProperty = itemSelectExpression.BindPropertyToSelectExpression(
                                property,
                                itemSelectExpression.ProjectStarTable.QuerySource);

                            itemSelectExpression.AddToProjection(itemProperty);

                            var collectionProperty = collectionSelectExpression.BindPropertyToSelectExpression(
                                property,
                                collectionSelectExpression.ProjectStarTable.QuerySource);

                            collectionSelectExpression.AddToProjection(collectionProperty);

                            var predicate = Expression.Equal(
                                collectionProperty.LiftExpressionFromSubquery(collectionSelectExpression),
                                itemProperty.LiftExpressionFromSubquery(itemSelectExpression));

                            joinExpression.Predicate
                                = joinExpression.Predicate == null
                                    ? predicate
                                    : Expression.AndAlso(
                                        joinExpression.Predicate,
                                        predicate);
                        }

                        SetConditionAsProjection(
                            handlerContext,
                            new ExistsExpression(outerSelectExpression));

                        return TransformClientExpression(handlerContext, typeof(bool));
                    }
                }

                SetConditionAsProjection(
                    handlerContext,
                    new InExpression(
                        item,
                        handlerContext.SelectExpression.Clone("")));

                return TransformClientExpression(handlerContext, typeof(bool));
            }

            return handlerContext.EvalOnClient();
        }

        private static Expression HandleCount(HandlerContext handlerContext)
        {
            if (handlerContext.SelectExpression.Offset != null)
            {
                handlerContext.SelectExpression.PushDownSubquery();
            }

            handlerContext.SelectExpression
                .SetProjectionExpression(
                    new SqlFunctionExpression(
                        "COUNT",
                        typeof(int),
                        new[] { new SqlFragmentExpression("*") }));

            handlerContext.SelectExpression.ClearOrderBy();

            return TransformClientExpression(handlerContext, typeof(int));
        }

        private static Expression HandleDefaultIfEmpty(HandlerContext handlerContext)
        {
            var defaultIfEmptyResultOperator = (DefaultIfEmptyResultOperator)handlerContext.ResultOperator;

            if (defaultIfEmptyResultOperator.OptionalDefaultValue != null)
            {
                return handlerContext.EvalOnClient();
            }

            var selectExpression = handlerContext.SelectExpression;

            selectExpression.PushDownSubquery();
            selectExpression.ExplodeStarProjection();

            var subquery = selectExpression.Tables.Single();

            selectExpression.ClearTables();

            var queryCompilationContext = handlerContext.QueryModelVisitor.QueryCompilationContext;
            var emptySelectExpression = handlerContext.SelectExpressionFactory.Create(queryCompilationContext, "empty");

            emptySelectExpression.AddToProjection(new AliasExpression("empty", Expression.Constant(null)));
            selectExpression.AddTable(emptySelectExpression);

            var leftOuterJoinExpression = new LeftOuterJoinExpression(subquery);
            var constant1 = Expression.Constant(1);

            leftOuterJoinExpression.Predicate = Expression.Equal(constant1, constant1);

            selectExpression.AddTable(leftOuterJoinExpression);

            selectExpression.ProjectStarTable = subquery;
            
            return new DefaultIfEmptyExpressionVisitor(queryCompilationContext)
                .Visit(handlerContext.QueryModelVisitor.Expression);
        }

        private sealed class DefaultIfEmptyExpressionVisitor : ExpressionVisitor
        {
            private readonly RelationalQueryCompilationContext _relationalQueryCompilationContext;

            public DefaultIfEmptyExpressionVisitor(RelationalQueryCompilationContext relationalQueryCompilationContext)
            {
                _relationalQueryCompilationContext = relationalQueryCompilationContext;
            }

            protected override Expression VisitExtension(Expression extensionExpression)
            {
                if (extensionExpression is ShapedQueryExpression shapedQueryExpression)
                {
                    return shapedQueryExpression.AsDefaultIfEmpty();
                }

                return base.VisitExtension(extensionExpression);
            }
        }

        private static Expression HandleDistinct(HandlerContext handlerContext)
        {
            if (!handlerContext.QueryModelVisitor.RequiresClientProjection)
            {
                handlerContext.SelectExpression.IsDistinct = true;

                return handlerContext.EvalOnServer;
            }

            return handlerContext.EvalOnClient();
        }

        private static Expression HandleFirst(HandlerContext handlerContext)
        {
            handlerContext.SelectExpression.Limit = Expression.Constant(1);

            var requiresClientResultOperator = !((FirstResultOperator)handlerContext.ResultOperator).ReturnDefaultWhenEmpty
                                               && handlerContext.QueryModelVisitor.ParentQueryModelVisitor != null;

            return handlerContext.EvalOnClient(requiresClientResultOperator);
        }

        private static Expression HandleGroup(HandlerContext handlerContext)
        {
            var sqlTranslatingExpressionVisitor = handlerContext.CreateSqlTranslatingVisitor();

            var groupResultOperator = (GroupResultOperator)handlerContext.ResultOperator;

            var sqlExpression
                = sqlTranslatingExpressionVisitor.Visit(groupResultOperator.KeySelector);

            if (sqlExpression != null)
            {
                var columns = (sqlExpression as ConstantExpression)?.Value as Expression[] ?? new[] { sqlExpression };

                handlerContext.SelectExpression
                    .PrependToOrderBy(columns.Select(c => new Ordering(c, OrderingDirection.Asc)));
            }

            var oldGroupByCall = (MethodCallExpression)handlerContext.EvalOnClient();

            var newGroupByCall
                = handlerContext.QueryModelVisitor.QueryCompilationContext.QueryMethodProvider.GroupByMethod;

            if (oldGroupByCall.Method.Name == "_GroupByAsync")
            {
                newGroupByCall = _groupByAsync;
            }

            return sqlExpression != null
                ? Expression.Call(newGroupByCall
                        .MakeGenericMethod(oldGroupByCall.Method.GetGenericArguments()),
                    oldGroupByCall.Arguments)
                : oldGroupByCall;
        }

        private static readonly MethodInfo _groupByAsync
            = typeof(RelationalResultOperatorHandler)
                .GetTypeInfo()
                .GetDeclaredMethod(nameof(_GroupByAsync));

        // ReSharper disable once InconsistentNaming
        private static IAsyncEnumerable<IGrouping<TKey, TElement>> _GroupByAsync<TSource, TKey, TElement>(
            IAsyncEnumerable<TSource> source,
            Func<TSource, TKey> keySelector,
            Func<TSource, CancellationToken, Task<TElement>> elementSelector)
            => new AsyncGroupByAsyncEnumerable<TSource, TKey, TElement>(source, keySelector, elementSelector);

        private sealed class AsyncGroupByAsyncEnumerable<TSource, TKey, TElement>
            : IAsyncEnumerable<IGrouping<TKey, TElement>>
        {
            private readonly IAsyncEnumerable<TSource> _source;
            private readonly Func<TSource, TKey> _keySelector;
            private readonly Func<TSource, CancellationToken, Task<TElement>> _elementSelector;

            public AsyncGroupByAsyncEnumerable(
                IAsyncEnumerable<TSource> source,
                Func<TSource, TKey> keySelector,
                Func<TSource, CancellationToken, Task<TElement>> elementSelector)
            {
                _source = source;
                _keySelector = keySelector;
                _elementSelector = elementSelector;
            }

            public IAsyncEnumerator<IGrouping<TKey, TElement>> GetEnumerator()
                => new GroupByAsyncEnumerator(this);

            private sealed class GroupByAsyncEnumerator : IAsyncEnumerator<IGrouping<TKey, TElement>>
            {
                private readonly AsyncGroupByAsyncEnumerable<TSource, TKey, TElement> _groupByAsyncEnumerable;
                private readonly IEqualityComparer<TKey> _comparer;

                private IAsyncEnumerator<TSource> _sourceEnumerator;
                private bool _hasNext;

                public GroupByAsyncEnumerator(
                    AsyncGroupByAsyncEnumerable<TSource, TKey, TElement> groupByAsyncEnumerable)
                {
                    _groupByAsyncEnumerable = groupByAsyncEnumerable;
                    _comparer = EqualityComparer<TKey>.Default;
                }

                public async Task<bool> MoveNext(CancellationToken cancellationToken)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (_sourceEnumerator == null)
                    {
                        _sourceEnumerator = _groupByAsyncEnumerable._source.GetEnumerator();
                        _hasNext = await _sourceEnumerator.MoveNext(cancellationToken);
                    }

                    if (_hasNext)
                    {
                        var currentKey = _groupByAsyncEnumerable._keySelector(_sourceEnumerator.Current);
                        var element = await _groupByAsyncEnumerable._elementSelector(_sourceEnumerator.Current, cancellationToken);
                        var grouping = new Grouping<TKey, TElement>(currentKey) { element };

                        while (true)
                        {
                            _hasNext = await _sourceEnumerator.MoveNext(cancellationToken);

                            if (!_hasNext)
                            {
                                break;
                            }

                            if (!_comparer.Equals(
                                currentKey,
                                _groupByAsyncEnumerable._keySelector(_sourceEnumerator.Current)))
                            {
                                break;
                            }

                            grouping.Add(await _groupByAsyncEnumerable._elementSelector(_sourceEnumerator.Current, cancellationToken));
                        }

                        Current = grouping;

                        return true;
                    }

                    return false;
                }

                public IGrouping<TKey, TElement> Current { get; private set; }

                public void Dispose() => _sourceEnumerator?.Dispose();
            }
        }

        private static Expression HandleLast(HandlerContext handlerContext)
        {
            var requiresClientResultOperator = true;
            if (handlerContext.SelectExpression.OrderBy.Any())
            {
                foreach (var ordering in handlerContext.SelectExpression.OrderBy)
                {
                    ordering.OrderingDirection
                        = ordering.OrderingDirection == OrderingDirection.Asc
                            ? OrderingDirection.Desc
                            : OrderingDirection.Asc;
                }

                handlerContext.SelectExpression.Limit = Expression.Constant(1);
                requiresClientResultOperator = false;
            }

            requiresClientResultOperator
                = requiresClientResultOperator
                || !((LastResultOperator)handlerContext.ResultOperator).ReturnDefaultWhenEmpty
                  && handlerContext.QueryModelVisitor.ParentQueryModelVisitor != null;

            return handlerContext.EvalOnClient(requiresClientResultOperator: requiresClientResultOperator);
        }

        private static Expression HandleLongCount(HandlerContext handlerContext)
        {
            if (handlerContext.SelectExpression.Offset != null)
            {
                handlerContext.SelectExpression.PushDownSubquery();
            }

            handlerContext.SelectExpression
                .SetProjectionExpression(
                    new SqlFunctionExpression(
                        "COUNT",
                        typeof(long),
                        new[] { new SqlFragmentExpression("*") }));

            handlerContext.SelectExpression.ClearOrderBy();

            return TransformClientExpression(handlerContext, typeof(long));
        }

        private static Expression HandleMin(HandlerContext handlerContext)
        {
            if (!handlerContext.QueryModelVisitor.RequiresClientProjection
                && handlerContext.SelectExpression.Projection.Count == 1)
            {
                var expression = handlerContext.SelectExpression.Projection.First();

                if (!(expression.RemoveConvert() is SelectExpression))
                {
                    var minExpression = new SqlFunctionExpression("MIN", handlerContext.QueryModel.SelectClause.Selector.Type, new [] { expression });

                    handlerContext.SelectExpression.SetProjectionExpression(minExpression);

                    return TransformClientExpression(handlerContext, minExpression.Type);
                }
            }

            return handlerContext.EvalOnClient();
        }

        private static Expression HandleMax(HandlerContext handlerContext)
        {
            if (!handlerContext.QueryModelVisitor.RequiresClientProjection
                && handlerContext.SelectExpression.Projection.Count == 1)
            {
                var expression = handlerContext.SelectExpression.Projection.First();

                if (!(expression.RemoveConvert() is SelectExpression))
                {
                    var maxExpression = new SqlFunctionExpression("MAX", handlerContext.QueryModel.SelectClause.Selector.Type, new [] { expression });

                    handlerContext.SelectExpression.SetProjectionExpression(maxExpression);

                    return TransformClientExpression(handlerContext, maxExpression.Type);
                }
            }

            return handlerContext.EvalOnClient();
        }

        private static Expression HandleSingle(HandlerContext handlerContext)
        {
            handlerContext.SelectExpression.Limit = Expression.Constant(2);

            var returnExpression = handlerContext.EvalOnClient();

            // For top level single, we do not require client eval
            if (handlerContext.QueryModelVisitor.ParentQueryModelVisitor == null)
            {
                handlerContext.QueryModelVisitor.RequiresClientResultOperator = false;
            }

            return returnExpression;
        }

        private static Expression HandleSkip(HandlerContext handlerContext)
        {
            var skipResultOperator = (SkipResultOperator)handlerContext.ResultOperator;

            var sqlTranslatingExpressionVisitor
                = handlerContext.CreateSqlTranslatingVisitor();

            var offset = sqlTranslatingExpressionVisitor.Visit(skipResultOperator.Count);
            if (offset != null)
            {
                handlerContext.SelectExpression.Offset = offset;

                return handlerContext.EvalOnServer;
            }

            return handlerContext.EvalOnClient();
        }

        private static Expression HandleSum(HandlerContext handlerContext)
        {
            if (!handlerContext.QueryModelVisitor.RequiresClientProjection
                && handlerContext.SelectExpression.Projection.Count == 1)
            {
                var expression = handlerContext.SelectExpression.Projection.First();

                if (!(expression.RemoveConvert() is SelectExpression))
                {
                    var sumExpression = new SqlFunctionExpression("SUM", handlerContext.QueryModel.SelectClause.Selector.Type, new [] { expression });

                    handlerContext.SelectExpression.SetProjectionExpression(sumExpression);

                    return TransformClientExpression(handlerContext, sumExpression.Type);
                }
            }

            return handlerContext.EvalOnClient();
        }

        private static Expression HandleTake(HandlerContext handlerContext)
        {
            var takeResultOperator = (TakeResultOperator)handlerContext.ResultOperator;

            var sqlTranslatingExpressionVisitor
                = handlerContext.CreateSqlTranslatingVisitor();

            var limit = sqlTranslatingExpressionVisitor.Visit(takeResultOperator.Count);

            if (limit != null)
            {
                handlerContext.SelectExpression.Limit = limit;

                return handlerContext.EvalOnServer;
            }

            return handlerContext.EvalOnClient();
        }

        private static void SetConditionAsProjection(
            HandlerContext handlerContext, Expression condition)
        {
            handlerContext.SelectExpression.Clear();

            handlerContext.SelectExpression.AddToProjection(
                Expression.Condition(condition,
                    Expression.Constant(true),
                    Expression.Constant(false),
                    typeof(bool)));
        }

        private static Expression TransformClientExpression(HandlerContext handlerContext, Type resultType)
            => new ResultTransformingExpressionVisitor(handlerContext.QueryModelVisitor.QueryCompilationContext, resultType)
                .Visit(handlerContext.QueryModelVisitor.Expression);
    }
}
