// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore.Extensions.Internal;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Internal;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Internal;
using Microsoft.EntityFrameworkCore.Query.Expressions;
using Microsoft.EntityFrameworkCore.Query.ExpressionVisitors;
using Microsoft.EntityFrameworkCore.Query.ExpressionVisitors.Internal;
using Microsoft.EntityFrameworkCore.Query.Internal;
using Microsoft.EntityFrameworkCore.Query.ResultOperators;
using Microsoft.EntityFrameworkCore.Query.ResultOperators.Internal;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.Storage.Internal;
using Microsoft.EntityFrameworkCore.Utilities;
using Remotion.Linq;
using Remotion.Linq.Clauses;
using Remotion.Linq.Clauses.Expressions;
using Remotion.Linq.Clauses.ExpressionVisitors;

namespace Microsoft.EntityFrameworkCore.Query
{
    /// <summary>
    ///     The default relational <see cref="QueryModel" /> visitor.
    /// </summary>
    public class RelationalQueryModelVisitor : EntityQueryModelVisitor
    {
        /// <summary>
        ///     The SelectExpressions for this query, mapped by query source.
        /// </summary>
        /// <value>
        ///     A map of query source to select expression.
        /// </value>
        protected virtual Dictionary<IQuerySource, SelectExpression> QueriesBySource { get; } =
            new Dictionary<IQuerySource, SelectExpression>();

        private readonly Dictionary<IQuerySource, RelationalQueryModelVisitor> _subQueryModelVisitorsBySource
            = new Dictionary<IQuerySource, RelationalQueryModelVisitor>();

        private readonly IRelationalAnnotationProvider _relationalAnnotationProvider;
        private readonly IIncludeExpressionVisitorFactory _includeExpressionVisitorFactory;
        private readonly ISqlTranslatingExpressionVisitorFactory _sqlTranslatingExpressionVisitorFactory;
        private readonly ICompositePredicateExpressionVisitorFactory _compositePredicateExpressionVisitorFactory;
        private readonly IConditionalRemovingExpressionVisitorFactory _conditionalRemovingExpressionVisitorFactory;

        private bool _requiresClientSelectMany;
        private bool _requiresClientJoin;
        private bool _requiresClientFilter;
        private bool _requiresClientProjection;
        private bool _requiresClientOrderBy;
        private bool _requiresClientResultOperator;
        private bool _requiresClientSingleColumnResultOperator;

        private Dictionary<IncludeSpecification, List<int>> _navigationIndexMap = new Dictionary<IncludeSpecification, List<int>>();

        /// <summary>
        ///     This API supports the Entity Framework Core infrastructure and is not intended to be used
        ///     directly from your code. This API may change or be removed in future releases.
        /// </summary>
        public RelationalQueryModelVisitor(
            [NotNull] EntityQueryModelVisitorDependencies dependencies,
            [NotNull] RelationalQueryModelVisitorDependencies relationalDependencies,
            [NotNull] RelationalQueryCompilationContext queryCompilationContext,
            [CanBeNull] RelationalQueryModelVisitor parentQueryModelVisitor)
            : base(
                dependencies.With(Check.NotNull(relationalDependencies, nameof(relationalDependencies)).RelationalResultOperatorHandler),
                queryCompilationContext)
        {
            _relationalAnnotationProvider = relationalDependencies.RelationalAnnotationProvider;
            _includeExpressionVisitorFactory = relationalDependencies.IncludeExpressionVisitorFactory;
            _sqlTranslatingExpressionVisitorFactory = relationalDependencies.SqlTranslatingExpressionVisitorFactory;
            _compositePredicateExpressionVisitorFactory = relationalDependencies.CompositePredicateExpressionVisitorFactory;
            _conditionalRemovingExpressionVisitorFactory = relationalDependencies.ConditionalRemovingExpressionVisitorFactory;

            ContextOptions = relationalDependencies.ContextOptions;
            ParentQueryModelVisitor = parentQueryModelVisitor;
        }

        /// <summary>
        ///     Gets the options for the target context.
        /// </summary>
        /// <value>
        ///     Options for the target context.
        /// </value>
        protected virtual IDbContextOptions ContextOptions { get; }

        /// <summary>
        ///     Gets or sets a value indicating whether the query requires client eval.
        /// </summary>
        /// <value>
        ///     true if the query requires client eval, false if not.
        /// </value>
        public virtual bool RequiresClientEval { get; set; }

        /// <summary>
        ///     Gets or sets a value indicating whether the query requires client select many.
        /// </summary>
        /// <value>
        ///     true if the query requires client select many, false if not.
        /// </value>
        public virtual bool RequiresClientSelectMany
        {
            get { return _requiresClientSelectMany || RequiresClientEval; }
            set { _requiresClientSelectMany = value; }
        }

        /// <summary>
        ///     Gets or sets a value indicating whether the query requires client join.
        /// </summary>
        /// <value>
        ///     true if the query requires client join, false if not.
        /// </value>
        public virtual bool RequiresClientJoin
        {
            get { return _requiresClientJoin || RequiresClientEval; }
            set { _requiresClientJoin = value; }
        }

        /// <summary>
        ///     Gets or sets a value indicating whether the query requires client filter.
        /// </summary>
        /// <value>
        ///     true if the query requires client filter, false if not.
        /// </value>
        public virtual bool RequiresClientFilter
        {
            get { return _requiresClientFilter || RequiresClientEval; }
            set { _requiresClientFilter = value; }
        }

        /// <summary>
        ///     Gets or sets a value indicating whether the query requires client order by.
        /// </summary>
        /// <value>
        ///     true if the query requires client order by, false if not.
        /// </value>
        public virtual bool RequiresClientOrderBy
        {
            get { return _requiresClientOrderBy || RequiresClientEval; }
            set { _requiresClientOrderBy = value; }
        }

        /// <summary>
        ///     Gets or sets a value indicating whether the query requires client projection.
        /// </summary>
        /// <value>
        ///     true if the query requires client projection, false if not.
        /// </value>
        public virtual bool RequiresClientProjection
        {
            get { return _requiresClientProjection || RequiresClientEval; }
            set { _requiresClientProjection = value; }
        }

        /// <summary>
        ///     Gets or sets a value indicating whether the query requires client result operator.
        /// </summary>
        /// <value>
        ///     true if the query requires client result operator, false if not.
        /// </value>
        public virtual bool RequiresClientResultOperator
        {
            get { return _requiresClientResultOperator || RequiresClientEval; }
            set { _requiresClientResultOperator = value; }
        }

        /// <summary>
        ///     Gets or sets a value indicating whether the query requires client evaluation for result operators potentially apply to a subset of
        ///     columns rather than entire row.
        /// </summary>
        /// <value>
        ///     true if the query requires client single column result operator, false if not.
        /// </value>
        internal virtual bool RequiresClientSingleColumnResultOperator
        {
            get { return _requiresClientSingleColumnResultOperator || _requiresClientResultOperator || RequiresClientEval; }
            set { _requiresClientSingleColumnResultOperator = value; }
        }

        /// <summary>
        ///     Gets or sets a value indicating whether this query model visitor will be
        ///     able to bind directly to properties from its parent query without requiring
        ///     parameter injection.
        /// </summary>
        /// <value>
        ///     true if the query model visitor can bind to its parent's properties, false if not.
        /// </value>
        public virtual bool CanBindToParentQueryModel { get; protected set; }
        
        /// <summary>
        ///     Gets a value indicating whether query model visitor's resulting expression
        ///     can be lifted into the parent query. Liftable queries contain a single SelectExpression.
        /// </summary>
        public virtual bool IsLiftable
        {
            get
            {
                return Queries.Count == 1
                    && !RequiresClientEval
                    && !RequiresClientSelectMany
                    && !RequiresClientJoin
                    && !RequiresClientFilter
                    && !RequiresClientProjection
                    && !RequiresClientOrderBy
                    && !RequiresClientResultOperator;
            }
        }

        /// <summary>
        ///     Context for the query compilation.
        /// </summary>
        public new virtual RelationalQueryCompilationContext QueryCompilationContext
            => (RelationalQueryCompilationContext)base.QueryCompilationContext;

        /// <summary>
        ///     The SelectExpressions active in the current query compilation.
        /// </summary>
        public virtual ICollection<SelectExpression> Queries => QueriesBySource.Values;

        /// <summary>
        ///     Gets the parent query model visitor, or null if there is no parent.
        /// </summary>
        /// <value>
        ///     The parent query model visitor, or null if there is no parent.
        /// </value>
        public virtual RelationalQueryModelVisitor ParentQueryModelVisitor { get; }

        /// <summary>
        ///     Registers a sub query visitor.
        /// </summary>
        /// <param name="querySource"> The query source. </param>
        /// <param name="queryModelVisitor"> The query model visitor. </param>
        public virtual void RegisterSubQueryVisitor(
            [NotNull] IQuerySource querySource, [NotNull] RelationalQueryModelVisitor queryModelVisitor)
        {
            Check.NotNull(querySource, nameof(querySource));
            Check.NotNull(queryModelVisitor, nameof(queryModelVisitor));

            _subQueryModelVisitorsBySource[querySource] = queryModelVisitor;
        }

        /// <summary>
        ///     Adds a SelectExpression to this query.
        /// </summary>
        /// <param name="querySource"> The query source. </param>
        /// <param name="selectExpression"> The select expression. </param>
        public virtual void AddQuery([NotNull] IQuerySource querySource, [NotNull] SelectExpression selectExpression)
        {
            Check.NotNull(querySource, nameof(querySource));
            Check.NotNull(selectExpression, nameof(selectExpression));

            QueriesBySource.Add(querySource, selectExpression);
        }

        /// <summary>
        ///     Try and get the active SelectExpression for a given query source.
        /// </summary>
        /// <param name="querySource"> The query source. </param>
        /// <returns>
        ///     A SelectExpression, or null.
        /// </returns>
        public virtual SelectExpression TryGetQuery([NotNull] IQuerySource querySource)
        {
            Check.NotNull(querySource, nameof(querySource));
            
            return QueriesBySource.TryGetValue(querySource, out var selectExpression)
                ? selectExpression
                : QueriesBySource.Values.LastOrDefault(se => se.HandlesQuerySource(querySource));
        }

        /// <summary>
        ///     High-level method called to perform Include compilation.
        /// </summary>
        /// <param name="queryModel"> The query model. </param>
        /// <param name="includeSpecifications"> Related data to be included. </param>
        protected override void IncludeNavigations(
            QueryModel queryModel,
            IReadOnlyCollection<IncludeSpecification> includeSpecifications)
        {
            Check.NotNull(queryModel, nameof(queryModel));
            Check.NotNull(includeSpecifications, nameof(includeSpecifications));

            _navigationIndexMap = BuildNavigationIndexMap(includeSpecifications);

            base.IncludeNavigations(queryModel, includeSpecifications);
        }

        private static Dictionary<IncludeSpecification, List<int>> BuildNavigationIndexMap(
            IEnumerable<IncludeSpecification> includeSpecifications)
        {
            var openedReaderCount = 0;
            var navigationIndexMap = new Dictionary<IncludeSpecification, List<int>>();

            foreach (var includeSpecification in includeSpecifications.Reverse())
            {
                var indexes = new List<int>();
                var openedNewReader = false;

                foreach (var navigation in includeSpecification.NavigationPath)
                {
                    if (navigation.IsCollection())
                    {
                        openedNewReader = true;
                        openedReaderCount++;
                        indexes.Add(openedReaderCount);
                    }
                    else
                    {
                        var index = openedNewReader ? openedReaderCount : 0;
                        indexes.Add(index);
                    }
                }

                navigationIndexMap.Add(includeSpecification, indexes);
            }

            return navigationIndexMap;
        }

        /// <summary>
        ///     High-level method called to perform Include compilation for a single Include.
        /// </summary>
        /// <param name="includeSpecification"> The navigation property to be included. </param>
        /// <param name="resultType"> The type of results returned by the query. </param>
        /// <param name="accessorExpression"> Expression for the navigation property to be included. </param>
        /// <param name="querySourceRequiresTracking"> A value indicating whether results of this query are to be tracked. </param>
        protected override void IncludeNavigations(
            IncludeSpecification includeSpecification,
            Type resultType,
            Expression accessorExpression,
            bool querySourceRequiresTracking)
        {
            Check.NotNull(includeSpecification, nameof(includeSpecification));
            Check.NotNull(resultType, nameof(resultType));

            var includeExpressionVisitor
                = _includeExpressionVisitorFactory.Create(
                    includeSpecification.QuerySource,
                    includeSpecification.NavigationPath,
                    QueryCompilationContext,
                    _navigationIndexMap[includeSpecification],
                    querySourceRequiresTracking);

            Expression = includeExpressionVisitor.Visit(Expression);
        }

        /// <summary>
        ///     Visit a query model.
        /// </summary>
        /// <param name="queryModel"> The query model. </param>
        public override void VisitQueryModel(QueryModel queryModel)
        {
            Check.NotNull(queryModel, nameof(queryModel));

            base.VisitQueryModel(queryModel);

            var compositePredicateVisitor = _compositePredicateExpressionVisitorFactory.Create();

            foreach (var selectExpression in QueriesBySource.Values)
            {
                compositePredicateVisitor.Visit(selectExpression);
            }
        }

        /// <summary>
        ///     Visit a sub-query model.
        /// </summary>
        /// <param name="queryModel"> The sub-query model. </param>
        public virtual void VisitSubQueryModel([NotNull] QueryModel queryModel)
        {
            CanBindToParentQueryModel = true;

            VisitQueryModel(queryModel);
        }

        /// <summary>
        ///     Compile main from clause expression.
        /// </summary>
        /// <param name="mainFromClause"> The main from clause. </param>
        /// <param name="queryModel"> The query model. </param>
        /// <returns>
        ///     An Expression.
        /// </returns>
        protected override Expression CompileMainFromClauseExpression(
            MainFromClause mainFromClause, QueryModel queryModel)
        {
            Check.NotNull(mainFromClause, nameof(mainFromClause));
            Check.NotNull(queryModel, nameof(queryModel));

            Expression expression = null;
            var subQueryExpression = mainFromClause.FromExpression as SubQueryExpression;
            if (subQueryExpression != null)
            {
                expression = LiftSubQuery(mainFromClause, subQueryExpression);
            }

            expression = expression ?? base.CompileMainFromClauseExpression(mainFromClause, queryModel);

            return expression;
        }

        /// <summary>
        ///     Visit an additional from clause.
        /// </summary>
        /// <param name="fromClause"> The from clause being visited. </param>
        /// <param name="queryModel"> The query model. </param>
        /// <param name="index"> Index of the node being visited. </param>
        public override void VisitAdditionalFromClause(
            AdditionalFromClause fromClause, QueryModel queryModel, int index)
        {
            Check.NotNull(fromClause, nameof(fromClause));
            Check.NotNull(queryModel, nameof(queryModel));

            var previousQuerySource = FindPreviousQuerySource(queryModel, index);
            var previousSelectExpression = TryGetQuery(previousQuerySource);
            var previousProjectionCount = previousSelectExpression?.Projection.Count ?? 0;

            base.VisitAdditionalFromClause(fromClause, queryModel, index);

            if (fromClause.FromExpression is QuerySourceReferenceExpression)
            {
                previousQuerySource = FindPreviousQuerySource(queryModel, index - 1);

                if (previousQuerySource != null && !RequiresClientJoin)
                {
                    previousSelectExpression = TryGetQuery(previousQuerySource);

                    if (previousSelectExpression != null)
                    {
                        AddQuery(fromClause, previousSelectExpression);
                    }
                }

                return;
            }

            if (!TryFlattenSelectMany(fromClause, queryModel, index, previousProjectionCount, false))
            {
                RequiresClientSelectMany = true;
                WarnClientEval(fromClause);
            }
        }

        /// <summary>
        ///     Visit a cross join lateral clause.
        /// </summary>
        /// <param name="crossJoinLateralClause"> The cross join lateral clause being visited. </param>
        /// <param name="queryModel"> The query model. </param>
        /// <param name="index"> Index of the node being visited. </param>
        public virtual void VisitCrossJoinLateralClause(
            [NotNull] CrossJoinLateralClause crossJoinLateralClause,
            [NotNull] QueryModel queryModel,
            int index)
        {
            Check.NotNull(crossJoinLateralClause, nameof(crossJoinLateralClause));
            Check.NotNull(queryModel, nameof(queryModel));

            var fromClause = crossJoinLateralClause.AdditionalFromClause;

            var previousQuerySource = FindPreviousQuerySource(queryModel, index);
            var previousSelectExpression = TryGetQuery(previousQuerySource);
            var previousProjectionCount = previousSelectExpression?.Projection.Count ?? 0;

            base.VisitAdditionalFromClause(fromClause, queryModel, index);

            if (!QueryCompilationContext.IsLateralJoinSupported
                || !TryFlattenSelectMany(fromClause, queryModel, index, previousProjectionCount, true))
            {
                RequiresClientSelectMany = true;
                WarnClientEval(crossJoinLateralClause.AdditionalFromClause);
            }
        }

        /// <summary>
        ///     Compile an additional from clause expression.
        /// </summary>
        /// <param name="additionalFromClause"> The additional from clause being compiled. </param>
        /// <param name="queryModel"> The query model. </param>
        /// <returns>
        ///     An Expression.
        /// </returns>
        protected override Expression CompileAdditionalFromClauseExpression(
            AdditionalFromClause additionalFromClause, QueryModel queryModel)
        {
            Check.NotNull(additionalFromClause, nameof(additionalFromClause));
            Check.NotNull(queryModel, nameof(queryModel));

            Expression expression = null;
            var subQueryExpression = additionalFromClause.FromExpression as SubQueryExpression;
            if (subQueryExpression != null)
            {
                expression = LiftSubQuery(additionalFromClause, subQueryExpression);
            }

            expression = expression ?? base.CompileAdditionalFromClauseExpression(additionalFromClause, queryModel);

            return expression;
        }

        /// <summary>
        ///     Visit a join clause.
        /// </summary>
        /// <param name="joinClause"> The join clause being visited. </param>
        /// <param name="queryModel"> The query model. </param>
        /// <param name="index"> Index of the node being visited. </param>
        public override void VisitJoinClause(
            JoinClause joinClause, QueryModel queryModel, int index)
        {
            Check.NotNull(joinClause, nameof(joinClause));
            Check.NotNull(queryModel, nameof(queryModel));

            var previousQuerySource = FindPreviousQuerySource(queryModel, index);
            var previousSelectExpression = TryGetQuery(previousQuerySource);
            var previousProjectionCount = previousSelectExpression?.Projection.Count ?? 0;

            base.VisitJoinClause(joinClause, queryModel, index);

            if (!TryFlattenJoin(joinClause, queryModel, index, previousProjectionCount))
            {
                RequiresClientJoin = true;
            }

            if (RequiresClientJoin)
            {
                WarnClientEval(joinClause);
            }
        }

        /// <summary>
        ///     Visit a left outer join clause.
        /// </summary>
        /// <param name="leftOuterJoinClause"> The left outer join clause being visited. </param>
        /// <param name="queryModel"> The query model. </param>
        /// <param name="index"> Index of the node being visited. </param>
        public virtual void VisitLeftOuterJoinClause(
            [NotNull] LeftOuterJoinClause leftOuterJoinClause,
            [NotNull] QueryModel queryModel, 
            int index)
        {
            Check.NotNull(leftOuterJoinClause, nameof(leftOuterJoinClause));
            Check.NotNull(queryModel, nameof(queryModel));

            if (!TryFlattenLeftJoin(leftOuterJoinClause, queryModel, index))
            {
                RequiresClientJoin = true;

                base.VisitGroupJoinClause(leftOuterJoinClause.GroupJoinClause, queryModel, index);
                base.VisitAdditionalFromClause(leftOuterJoinClause.AdditionalFromClause, queryModel, index);

                WarnClientEval(leftOuterJoinClause.GroupJoinClause.JoinClause);
            }

            // Workaround until #6647 is addressed - GroupJoin requires materialization of entire entity which results in all columns of that entity being projected
            // this in turn causes result operators to be applied on all of those columns, even if the query specifies a subset of columns to perform the operation on
            // this could lead to incorrect results (e.g. for Distinct)
            // This however is safe to do for some operators, e.g. FirstOrDefault, Count(), Take() because their result is the same whether they are applied on single column or entire row
            RequiresClientSingleColumnResultOperator = true;
        }

        /// <summary>
        ///     Compile a join clause inner sequence expression.
        /// </summary>
        /// <param name="joinClause"> The join clause being compiled. </param>
        /// <param name="queryModel"> The query model. </param>
        /// <returns>
        ///     An Expression.
        /// </returns>
        protected override Expression CompileJoinClauseInnerSequenceExpression(
            JoinClause joinClause, QueryModel queryModel)
        {
            Check.NotNull(joinClause, nameof(joinClause));
            Check.NotNull(queryModel, nameof(queryModel));

            Expression expression = null;
            var subQueryExpression = joinClause.InnerSequence as SubQueryExpression;
            if (subQueryExpression != null)
            {
                expression = LiftSubQuery(joinClause, subQueryExpression);
            }

            expression = expression ?? base.CompileJoinClauseInnerSequenceExpression(joinClause, queryModel);

            return expression;
        }

        /// <summary>
        ///     Visit a group join clause.
        /// </summary>
        /// <param name="groupJoinClause"> The group join being visited. </param>
        /// <param name="queryModel"> The query model. </param>
        /// <param name="index"> Index of the node being visited. </param>
        public override void VisitGroupJoinClause(
            GroupJoinClause groupJoinClause, QueryModel queryModel, int index)
        {
            Check.NotNull(groupJoinClause, nameof(groupJoinClause));
            Check.NotNull(queryModel, nameof(queryModel));

            var previousQuerySource = FindPreviousQuerySource(queryModel, index);
            var previousSelectExpression = TryGetQuery(previousQuerySource);
            var previousProjectionCount = previousSelectExpression?.Projection.Count ?? 0;

            base.VisitGroupJoinClause(groupJoinClause, queryModel, index);

            if (!TryFlattenGroupJoin(
                groupJoinClause,
                queryModel,
                index,
                previousProjectionCount))
            {
                RequiresClientJoin = true;
            }

            if (RequiresClientJoin)
            {
                WarnClientEval(groupJoinClause.JoinClause);
            }

            // Workaround until #6647 is addressed - GroupJoin requires materialization of entire entity which results in all columns of that entity being projected
            // this in turn causes result operators to be applied on all of those columns, even if the query specifies a subset of columns to perform the operation on
            // this could lead to incorrect results (e.g. for Distinct)
            // This however is safe to do for some operators, e.g. FirstOrDefault, Count(), Take() because their result is the same whether they are applied on single column or entire row
            RequiresClientSingleColumnResultOperator = true;
        }

        private class OuterJoinOrderingExtractor : ExpressionVisitor
        {
            private readonly List<Expression> _expressions = new List<Expression>();

            public bool DependentToPrincipalFound { get; private set; }

            public IEnumerable<Expression> Expressions => _expressions;

            private IForeignKey _matchingCandidate;
            private List<IProperty> _matchingCandidateProperties;

            public override Expression Visit(Expression expression)
            {
                var binaryExpression = expression as BinaryExpression;

                if (binaryExpression != null)
                {
                    return VisitBinary(binaryExpression);
                }

                return expression;
            }

            protected override Expression VisitBinary(BinaryExpression node)
            {
                if (DependentToPrincipalFound)
                {
                    return node;
                }

                if (node.NodeType == ExpressionType.Equal)
                {
                    var leftProperty = node.Left.RemoveConvert().TryGetColumnExpression()?.Property;
                    var rightProperty = node.Right.RemoveConvert().TryGetColumnExpression()?.Property;
                    if (leftProperty != null
                        && rightProperty != null
                        && leftProperty.IsForeignKey()
                        && rightProperty.IsKey())
                    {
                        var keyDeclaringEntityType = rightProperty.GetContainingKeys().First().DeclaringEntityType;
                        var matchingForeignKeys = leftProperty.GetContainingForeignKeys().Where(k => k.PrincipalKey.DeclaringEntityType == keyDeclaringEntityType);
                        if (matchingForeignKeys.Count() == 1)
                        {
                            var matchingKey = matchingForeignKeys.Single();
                            if (rightProperty.GetContainingKeys().Contains(matchingKey.PrincipalKey))
                            {
                                var matchingForeignKey = matchingKey;
                                if (_matchingCandidate == null)
                                {
                                    _matchingCandidate = matchingForeignKey;
                                    _matchingCandidateProperties = new List<IProperty> { leftProperty };
                                }
                                else if (_matchingCandidate == matchingForeignKey)
                                {
                                    _matchingCandidateProperties.Add(leftProperty);
                                }

                                if (_matchingCandidate.Properties.All(p => _matchingCandidateProperties.Contains(p)))
                                {
                                    DependentToPrincipalFound = true;
                                    return node;
                                }
                            }
                        }
                    }

                    _expressions.Add(node.Left.RemoveConvert());

                    return node;
                }

                if (node.NodeType == ExpressionType.AndAlso)
                {
                    return base.VisitBinary(node);
                }

                return node;
            }
        }

        private static IQuerySource FindPreviousQuerySource(QueryModel queryModel, int index)
        {
            for (var i = index; i >= 0; i--)
            {
                var candidate = i == 0
                    ? queryModel.MainFromClause
                    : queryModel.BodyClauses[i - 1] as IQuerySource;

                if (candidate != null)
                {
                    return candidate;
                }
            }

            return null;
        }

        /// <summary>
        ///     Compile a group join inner sequence expression.
        /// </summary>
        /// <param name="groupJoinClause"> The group join clause being compiled. </param>
        /// <param name="queryModel"> The query model. </param>
        /// <returns>
        ///     An Expression.
        /// </returns>
        protected override Expression CompileGroupJoinInnerSequenceExpression(
            GroupJoinClause groupJoinClause, QueryModel queryModel)
        {
            Check.NotNull(groupJoinClause, nameof(groupJoinClause));
            Check.NotNull(queryModel, nameof(queryModel));

            Expression expression = null;
            var subQueryExpression = groupJoinClause.JoinClause.InnerSequence as SubQueryExpression;
            if (subQueryExpression != null)
            {
                expression = LiftSubQuery(groupJoinClause.JoinClause, subQueryExpression);
            }

            expression = expression ?? base.CompileGroupJoinInnerSequenceExpression(groupJoinClause, queryModel);

            return expression;
        }

        private Expression LiftSubQuery(
            IQuerySource querySource, SubQueryExpression subQueryExpression)
        {
            var subQueryModelVisitor
                = (RelationalQueryModelVisitor)QueryCompilationContext
                    .CreateQueryModelVisitor(this);

            subQueryModelVisitor.VisitSubQueryModel(subQueryExpression.QueryModel);

            if (subQueryModelVisitor.IsLiftable)
            {
                var subSelectExpression = subQueryModelVisitor.Queries.First();

                if ((!subSelectExpression.OrderBy.Any()
                     || subSelectExpression.Limit != null
                     || subSelectExpression.Offset != null)
                    && (QueryCompilationContext.IsLateralJoinSupported
                        || !subSelectExpression.IsCorrelated()
                        || !(querySource is AdditionalFromClause)))
                {
                    if (!subSelectExpression.IsIdentityQuery())
                    {
                        subSelectExpression.PushDownSubquery().QuerySource = querySource;
                    }

                    AddQuery(querySource, subSelectExpression);

                    var newExpression
                        = new QuerySourceUpdater(
                                querySource,
                                QueryCompilationContext,
                                LinqOperatorProvider,
                                subSelectExpression)
                            .Visit(subQueryModelVisitor.Expression);

                    return newExpression;
                }
            }

            return null;
        }

        private sealed class QuerySourceUpdater : ExpressionVisitorBase
        {
            private readonly IQuerySource _querySource;
            private readonly RelationalQueryCompilationContext _relationalQueryCompilationContext;
            private readonly ILinqOperatorProvider _linqOperatorProvider;
            private readonly SelectExpression _selectExpression;
            private bool _insideShapedQueryMethod;

            public QuerySourceUpdater(
                IQuerySource querySource,
                RelationalQueryCompilationContext relationalQueryCompilationContext,
                ILinqOperatorProvider linqOperatorProvider,
                SelectExpression selectExpression)
            {
                _querySource = querySource;
                _relationalQueryCompilationContext = relationalQueryCompilationContext;
                _linqOperatorProvider = linqOperatorProvider;
                _selectExpression = selectExpression;
            }

            protected override Expression VisitConstant(ConstantExpression constantExpression)
            {
                var shaper = constantExpression.Value as Shaper;

                if (shaper != null)
                {
                    foreach (var queryAnnotation
                        in _relationalQueryCompilationContext.QueryAnnotations
                            .Where(qa => shaper.IsShaperForQuerySource(qa.QuerySource)))
                    {
                        queryAnnotation.QuerySource = _querySource;
                    }

                    if (_insideShapedQueryMethod
                        && shaper is EntityShaper
                        && !_relationalQueryCompilationContext.QuerySourceRequiresMaterialization(_querySource))
                    {
                        return Expression.Constant(new ValueBufferShaper(_querySource));
                    }

                    shaper.UpdateQuerySource(_querySource);

                    _selectExpression.ExplodeStarProjection();
                }

                return base.VisitConstant(constantExpression);
            }

            protected override Expression VisitMethodCall(MethodCallExpression methodCallExpression)
            {
                _insideShapedQueryMethod = methodCallExpression.Method.MethodIsClosedFormOf(
                    _relationalQueryCompilationContext.QueryMethodProvider.ShapedQueryMethod);

                var arguments = VisitAndConvert(methodCallExpression.Arguments, "VisitMethodCall");

                if (arguments != methodCallExpression.Arguments)
                {
                    if (_insideShapedQueryMethod)
                    {
                        return Expression.Call(
                            _relationalQueryCompilationContext.QueryMethodProvider.ShapedQueryMethod
                                .MakeGenericMethod(((Shaper)((ConstantExpression)arguments[2]).Value).Type),
                            arguments);
                    }

                    if (methodCallExpression.Method.MethodIsClosedFormOf(
                            _linqOperatorProvider.Cast)
                        && arguments[0].Type.GetSequenceType() == typeof(ValueBuffer))
                    {
                        return arguments[0];
                    }
                }

                return base.VisitMethodCall(methodCallExpression);
            }

            protected override Expression VisitLambda<T>(Expression<T> lambdaExpression)
            {
                Check.NotNull(lambdaExpression, nameof(lambdaExpression));

                var newBodyExpression = Visit(lambdaExpression.Body);

                return newBodyExpression != lambdaExpression.Body
                    ? Expression.Lambda(newBodyExpression, lambdaExpression.Parameters)
                    : lambdaExpression;
            }
        }

        /// <summary>
        ///     Visit a where clause.
        /// </summary>
        /// <param name="whereClause"> The where clause being visited. </param>
        /// <param name="queryModel"> The query model. </param>
        /// <param name="index"> Index of the node being visited. </param>
        public override void VisitWhereClause(WhereClause whereClause, QueryModel queryModel, int index)
        {
            Check.NotNull(whereClause, nameof(whereClause));
            Check.NotNull(queryModel, nameof(queryModel));

            var selectExpression = TryGetQuery(queryModel.MainFromClause);
            var requiresClientFilter = selectExpression == null;

            if (!requiresClientFilter)
            {
                var sqlTranslatingExpressionVisitor
                    = _sqlTranslatingExpressionVisitorFactory.Create(
                        queryModelVisitor: this,
                        targetSelectExpression: selectExpression,
                        topLevelPredicate: whereClause.Predicate);

                var sqlPredicateExpression = sqlTranslatingExpressionVisitor.Visit(whereClause.Predicate);

                if (sqlPredicateExpression != null)
                {
                    sqlPredicateExpression =
                        _conditionalRemovingExpressionVisitorFactory
                            .Create()
                            .Visit(sqlPredicateExpression);
                    
                    selectExpression.AddToPredicate(sqlPredicateExpression);
                }
                else
                {
                    requiresClientFilter = true;
                }

                if (sqlTranslatingExpressionVisitor.ClientEvalPredicate != null
                    && selectExpression.Predicate != null)
                {
                    requiresClientFilter = true;
                    whereClause = new WhereClause(sqlTranslatingExpressionVisitor.ClientEvalPredicate);
                }
            }

            RequiresClientFilter |= requiresClientFilter;

            if (RequiresClientFilter)
            {
                WarnClientEval(whereClause.Predicate);

                base.VisitWhereClause(whereClause, queryModel, index);
            }
        }

        /// <summary>
        ///     Visit an order by clause.
        /// </summary>
        /// <param name="orderByClause"> The order by clause. </param>
        /// <param name="queryModel"> The query model. </param>
        /// <param name="index"> Index of the node being visited. </param>
        public override void VisitOrderByClause(OrderByClause orderByClause, QueryModel queryModel, int index)
        {
            Check.NotNull(orderByClause, nameof(orderByClause));
            Check.NotNull(queryModel, nameof(queryModel));

            var selectExpression = TryGetQuery(queryModel.MainFromClause);
            var requiresClientOrderBy = selectExpression == null;

            if (!requiresClientOrderBy)
            {
                var sqlTranslatingExpressionVisitor
                    = _sqlTranslatingExpressionVisitorFactory.Create(
                        queryModelVisitor: this,
                        targetSelectExpression: selectExpression);

                var orderings = new List<Ordering>();

                foreach (var ordering in orderByClause.Orderings)
                {
                    // we disable this for order by, because you can't have a parameter (that is integer) in the order by
                    var canBindPropertyToOuterParameter = _canBindPropertyToOuterParameter;
                    _canBindPropertyToOuterParameter = false;

                    var sqlOrderingExpression
                        = sqlTranslatingExpressionVisitor
                            .Visit(ordering.Expression);

                    _canBindPropertyToOuterParameter = canBindPropertyToOuterParameter;

                    if (sqlOrderingExpression == null)
                    {
                        break;
                    }

                    if (sqlOrderingExpression.IsComparisonOperation()
                        || sqlOrderingExpression.IsLogicalOperation())
                    {
                        sqlOrderingExpression = Expression.Condition(
                            sqlOrderingExpression,
                            Expression.Constant(true, typeof(bool)),
                            Expression.Constant(false, typeof(bool)));
                    }

                    orderings.Add(
                        new Ordering(
                            sqlOrderingExpression,
                            ordering.OrderingDirection));
                }

                if (orderings.Count == orderByClause.Orderings.Count)
                {
                    selectExpression.PrependToOrderBy(orderings);
                }
                else
                {
                    requiresClientOrderBy = true;
                }
            }

            RequiresClientOrderBy |= requiresClientOrderBy;

            if (RequiresClientOrderBy)
            {
                WarnClientEval(orderByClause);

                base.VisitOrderByClause(orderByClause, queryModel, index);
            }
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

            base.VisitSelectClause(selectClause, queryModel);

            if (Expression is MethodCallExpression methodCallExpression
                && methodCallExpression.Method.MethodIsClosedFormOf(LinqOperatorProvider.Select))
            {
                var shapedQuery = methodCallExpression.Arguments[0] as MethodCallExpression;

                if (IsShapedQueryExpression(shapedQuery))
                {
                    shapedQuery = UnwrapShapedQueryExpression(shapedQuery);

                    var oldShaper = ExtractShaper(shapedQuery, 0);

                    var matchingIncludes
                        = from i in QueryCompilationContext.QueryAnnotations.OfType<IncludeResultOperator>()
                          where oldShaper.IsShaperForQuerySource(i.QuerySource)
                          select i;

                    if (!matchingIncludes.Any())
                    {
                        var materializer = (LambdaExpression)methodCallExpression.Arguments[1];
                        var qsreFinder = new QuerySourceReferenceFindingExpressionVisitor();

                        qsreFinder.Visit(materializer.Body);

                        if (!qsreFinder.FoundAny)
                        {
                            var newShaper = ProjectionShaper.Create(oldShaper, materializer);

                            Expression =
                                Expression.Call(
                                    shapedQuery.Method
                                        .GetGenericMethodDefinition()
                                        .MakeGenericMethod(Expression.Type.GetSequenceType()),
                                    shapedQuery.Arguments[0],
                                    shapedQuery.Arguments[1],
                                    Expression.Constant(newShaper));
                        }
                    }
                }
            }
        }

        private class QuerySourceReferenceFindingExpressionVisitor : ExpressionVisitorBase
        {
            public bool FoundAny { get; private set; }

            protected override Expression VisitQuerySourceReference(QuerySourceReferenceExpression expression)
            {
                FoundAny = true;

                return base.VisitQuerySourceReference(expression);
            }
        }

        /// <summary>
        ///     Visit a result operator.
        /// </summary>
        /// <param name="resultOperator"> The result operator being visited. </param>
        /// <param name="queryModel"> The query model. </param>
        /// <param name="index"> Index of the node being visited. </param>
        public override void VisitResultOperator(ResultOperatorBase resultOperator, QueryModel queryModel, int index)
        {
            base.VisitResultOperator(resultOperator, queryModel, index);

            if (RequiresClientResultOperator)
            {
                WarnClientEval(resultOperator);
            }
        }

        /// <summary>
        ///     Applies optimizations to the query.
        /// </summary>
        /// <param name="queryModel"> The query. </param>
        /// <param name="includeResultOperators">TODO: This parameter is to be removed.</param>
        protected override void OptimizeQueryModel(
            QueryModel queryModel,
            ICollection<IncludeResultOperator> includeResultOperators)
        {
            Check.NotNull(queryModel, nameof(queryModel));
            Check.NotNull(includeResultOperators, nameof(includeResultOperators));

            var typeIsExpressionTranslatingVisitor
                = new TypeIsExpressionTranslatingVisitor(QueryCompilationContext.Model, _relationalAnnotationProvider);

            queryModel.TransformExpressions(typeIsExpressionTranslatingVisitor.Visit);

            base.OptimizeQueryModel(queryModel, includeResultOperators);

            new LeftOuterJoinClauseQueryModelVisitor(QueryCompilationContext.QueryAnnotations)
                .VisitQueryModel(queryModel);

            new CrossJoinLateralClauseQueryModelVisitor(QueryCompilationContext.QueryAnnotations)
                .VisitQueryModel(queryModel);
        }

        /// <summary>
        ///     Generated a client-eval warning
        /// </summary>
        /// <param name="expression"> The expression being client-eval'd. </param>
        protected virtual void WarnClientEval([NotNull] object expression)
        {
            Check.NotNull(expression, nameof(expression));

            QueryCompilationContext.Logger.LogWarning(
                RelationalEventId.QueryClientEvaluationWarning,
                () => RelationalStrings.ClientEvalWarning(expression));
        }

        private class TypeIsExpressionTranslatingVisitor : ExpressionVisitorBase
        {
            private readonly IModel _model;
            private readonly IRelationalAnnotationProvider _relationalAnnotationProvider;

            public TypeIsExpressionTranslatingVisitor(IModel model, IRelationalAnnotationProvider relationalAnnotationProvider)
            {
                _model = model;
                _relationalAnnotationProvider = relationalAnnotationProvider;
            }

            protected override Expression VisitTypeBinary(TypeBinaryExpression typeBinaryExpression)
            {
                if (typeBinaryExpression.NodeType != ExpressionType.TypeIs)
                {
                    return base.VisitTypeBinary(typeBinaryExpression);
                }

                var entityType = _model.FindEntityType(typeBinaryExpression.TypeOperand);

                if (entityType == null)
                {
                    return base.VisitTypeBinary(typeBinaryExpression);
                }

                var concreteEntityTypes
                    = entityType.GetConcreteTypesInHierarchy().ToList();

                if (concreteEntityTypes.Count != 1
                    || concreteEntityTypes[0].RootType() != concreteEntityTypes[0])
                {
                    var discriminatorProperty
                        = _relationalAnnotationProvider.For(concreteEntityTypes[0]).DiscriminatorProperty;

                    var discriminatorPropertyExpression = CreatePropertyExpression(typeBinaryExpression.Expression, discriminatorProperty);

                    var discriminatorPredicate
                        = concreteEntityTypes
                            .Select(concreteEntityType =>
                                Expression.Equal(
                                    discriminatorPropertyExpression,
                                    Expression.Constant(_relationalAnnotationProvider.For(concreteEntityType).DiscriminatorValue, discriminatorPropertyExpression.Type)))
                            .Aggregate((current, next) => Expression.OrElse(next, current));

                    return discriminatorPredicate;
                }

                return Expression.Constant(true, typeof(bool));
            }
        }

        #region Flattening

        private bool IsShapedQueryExpression(Expression expression)
        {
            var methodCallExpression = expression as MethodCallExpression;

            if (methodCallExpression == null)
            {
                return false;
            }

            var linqMethods = QueryCompilationContext.LinqOperatorProvider;

            if (methodCallExpression.Method.MethodIsClosedFormOf(linqMethods.DefaultIfEmpty) 
                || methodCallExpression.Method.MethodIsClosedFormOf(linqMethods.DefaultIfEmptyArg))
            {
                methodCallExpression = methodCallExpression.Arguments[0] as MethodCallExpression;

                if (methodCallExpression == null)
                {
                    return false;
                }
            }

            var queryMethods = QueryCompilationContext.QueryMethodProvider;

            if (methodCallExpression.Method.MethodIsClosedFormOf(queryMethods.ShapedQueryMethod) 
                || methodCallExpression.Method.MethodIsClosedFormOf(queryMethods.DefaultIfEmptyShapedQueryMethod))
            {
                return true;
            }

            return false;
        }

        private MethodCallExpression UnwrapShapedQueryExpression(MethodCallExpression expression)
        {
            if (expression.Method.MethodIsClosedFormOf(LinqOperatorProvider.DefaultIfEmpty)
                || expression.Method.MethodIsClosedFormOf(LinqOperatorProvider.DefaultIfEmptyArg))
            {
                return (MethodCallExpression)expression.Arguments[0];
            }

            return expression;
        }

        private Shaper ExtractShaper(MethodCallExpression shapedQueryExpression, int offset)
        {
            var shaper = (Shaper)((ConstantExpression)UnwrapShapedQueryExpression(shapedQueryExpression).Arguments[2]).Value;

            return shaper.WithOffset(offset);
        }

        private bool TryFlattenSelectMany(
            AdditionalFromClause fromClause, 
            QueryModel queryModel, 
            int index,
            int previousProjectionCount,
            bool correlated)
        {
            if (RequiresClientJoin || RequiresClientSelectMany)
            {
                return false;
            }

            var outerQuerySource = FindPreviousQuerySource(queryModel, index);
            var outerSelectExpression = TryGetQuery(outerQuerySource);
            var innerSelectExpression = TryGetQuery(fromClause);

            if (outerSelectExpression == null || innerSelectExpression == null)
            {
                return false;
            }

            var selectManyMethodCallExpression = Expression as MethodCallExpression;

            var outerShapedQuery
                = selectManyMethodCallExpression?.Arguments.FirstOrDefault() as MethodCallExpression;

            var innerShapedQuery
                = (selectManyMethodCallExpression?.Arguments.Skip(1).FirstOrDefault() as LambdaExpression)
                    ?.Body as MethodCallExpression;

            if (selectManyMethodCallExpression == null
                || !selectManyMethodCallExpression.Method.MethodIsClosedFormOf(LinqOperatorProvider.SelectMany)
                || !IsShapedQueryExpression(outerShapedQuery) 
                || !IsShapedQueryExpression(innerShapedQuery))
            {
                return false;
            }

            if (!QueryCompilationContext.QuerySourceRequiresMaterialization(outerQuerySource))
            {
                outerSelectExpression.RemoveRangeFromProjection(previousProjectionCount);
            }

            var outerProjectionCount = outerSelectExpression.Projection.Count;

            var tableToJoin
                = innerSelectExpression.Tables.Count == 1
                    ? innerSelectExpression.Tables[0]
                    : innerSelectExpression;

            var joinExpression
                = correlated
                    ? outerSelectExpression.AddCrossJoinLateral(
                        tableToJoin,
                        innerSelectExpression.Projection)
                    : outerSelectExpression.AddCrossJoin(
                        tableToJoin,
                        innerSelectExpression.Projection);

            joinExpression.QuerySource = fromClause;

            QueriesBySource.Remove(fromClause);

            var outerShaper = ExtractShaper(outerShapedQuery, 0);
            var innerShaper = ExtractShaper(innerShapedQuery, previousProjectionCount);

            var materializerLambda = (LambdaExpression)selectManyMethodCallExpression.Arguments.Last();
            var materializer = materializerLambda.Compile();

            var compositeShaper
                = CompositeShaper.Create(fromClause, outerShaper, innerShaper, materializer);

            compositeShaper.SaveAccessorExpression(QueryCompilationContext.QuerySourceMapping);

            innerShaper.UpdateQuerySource(fromClause);

            Expression
                = Expression.Call(
                    outerShapedQuery.Method
                        .GetGenericMethodDefinition()
                        .MakeGenericMethod(materializerLambda.ReturnType),
                    outerShapedQuery.Arguments[0],
                    outerShapedQuery.Arguments[1],
                    Expression.Constant(compositeShaper));

            return true;
        }

        private bool TryFlattenJoin(
            JoinClause joinClause, 
            QueryModel queryModel, 
            int index, 
            int previousProjectionCount)
        {
            if (RequiresClientJoin || RequiresClientSelectMany)
            {
                return false;
            }

            var joinMethodCallExpression = Expression as MethodCallExpression;

            var outerShapedQuery
                = joinMethodCallExpression?.Arguments.FirstOrDefault() as MethodCallExpression;

            var innerShapedQuery
                = joinMethodCallExpression?.Arguments.Skip(1).FirstOrDefault() as MethodCallExpression;

            if (joinMethodCallExpression == null
                || !joinMethodCallExpression.Method.MethodIsClosedFormOf(LinqOperatorProvider.Join)
                || !IsShapedQueryExpression(outerShapedQuery) 
                || !IsShapedQueryExpression(innerShapedQuery))
            {
                return false;
            }

            var outerQuerySource = FindPreviousQuerySource(queryModel, index);
            var outerSelectExpression = TryGetQuery(outerQuerySource);
            var innerSelectExpression = TryGetQuery(joinClause);

            if (outerSelectExpression == null || innerSelectExpression == null)
            {
                return false;
            }

            var sqlTranslatingExpressionVisitor
                = _sqlTranslatingExpressionVisitorFactory.Create(this);

            var predicate
                = sqlTranslatingExpressionVisitor.Visit(
                    Expression.Equal(joinClause.OuterKeySelector, joinClause.InnerKeySelector));

            if (predicate == null)
            {
                return false;
            }

            QueriesBySource.Remove(joinClause);

            outerSelectExpression.RemoveRangeFromProjection(previousProjectionCount);

            var projection
                = QueryCompilationContext.QuerySourceRequiresMaterialization(joinClause)
                    ? innerSelectExpression.Projection
                    : Enumerable.Empty<Expression>();

            var joinExpression
                = outerSelectExpression.AddInnerJoin(
                    innerSelectExpression.Tables.Single(),
                    projection,
                    innerSelectExpression.Predicate);

            joinExpression.Predicate = predicate;
            joinExpression.QuerySource = joinClause;

            var outerShaper = ExtractShaper(outerShapedQuery, 0);
            var innerShaper = ExtractShaper(innerShapedQuery, previousProjectionCount);

            var materializerLambda = (LambdaExpression)joinMethodCallExpression.Arguments.Last();
            var materializer = materializerLambda.Compile();

            var compositeShaper
                = CompositeShaper.Create(joinClause, outerShaper, innerShaper, materializer);

            compositeShaper.SaveAccessorExpression(QueryCompilationContext.QuerySourceMapping);

            innerShaper.UpdateQuerySource(joinClause);

            Expression
                = Expression.Call(
                    outerShapedQuery.Method
                        .GetGenericMethodDefinition()
                        .MakeGenericMethod(materializerLambda.ReturnType),
                    outerShapedQuery.Arguments[0],
                    outerShapedQuery.Arguments[1],
                    Expression.Constant(compositeShaper));

            return true;
        }

        private bool TryFlattenLeftJoin(
            LeftOuterJoinClause leftOuterJoinClause,
            QueryModel queryModel,
            int index)
        {
            if (RequiresClientJoin || RequiresClientSelectMany)
            {
                return false;
            }

            var outerShapedQuery = Expression as MethodCallExpression;
            var outerQuerySource = FindPreviousQuerySource(queryModel, index);
            var outerSelectExpression = TryGetQuery(outerQuerySource);

            if (!IsShapedQueryExpression(outerShapedQuery) || outerSelectExpression == null)
            {
                return false;
            }

            var groupJoinClause = leftOuterJoinClause.GroupJoinClause;
            var joinClause = leftOuterJoinClause.GroupJoinClause.JoinClause;
            var additionalFromClause = leftOuterJoinClause.AdditionalFromClause;

            var sqlTranslator = _sqlTranslatingExpressionVisitorFactory.Create(this);
            var sqlOuterKeySelectorExpression = sqlTranslator.Visit(joinClause.OuterKeySelector);

            if (sqlOuterKeySelectorExpression == null)
            {
                return false;
            }

            var innerSequenceExpression = CompileGroupJoinInnerSequenceExpression(groupJoinClause, queryModel);
            var innerShapedQuery = innerSequenceExpression as MethodCallExpression;
            var innerSelectExpression = TryGetQuery(joinClause);

            if (!IsShapedQueryExpression(innerShapedQuery) || innerSelectExpression == null)
            {
                return false;
            }

            var sqlInnerKeySelectorExpression = sqlTranslator.Visit(joinClause.InnerKeySelector);

            if (sqlInnerKeySelectorExpression == null)
            {
                return false;
            }
            else if (innerSelectExpression.Predicate != null)
            {
                var pushedDownSubQuery = innerSelectExpression.PushDownSubquery();
                innerSelectExpression.ExplodeStarProjection();
                pushedDownSubQuery.ClearProjection();
            }

            var predicate
                = sqlTranslator.Visit(
                    Expression.Equal(
                        joinClause.OuterKeySelector,
                        joinClause.InnerKeySelector));

            QueriesBySource.Remove(joinClause);

            var outerShaper = ExtractShaper(outerShapedQuery, 0);
            var innerShaper = ExtractShaper(innerShapedQuery, outerSelectExpression.Projection.Count);

            innerShaper.UpdateQuerySource(additionalFromClause);

            var projection
                = QueryCompilationContext.QuerySourceRequiresMaterialization(joinClause)
                    ? innerSelectExpression.Projection
                    : Enumerable.Empty<Expression>();

            var joinExpression
                = outerSelectExpression.AddLeftOuterJoin(
                    innerSelectExpression.Tables.Single(),
                    projection);

            joinExpression.Predicate = predicate;
            joinExpression.QuerySource = additionalFromClause;

            var innerItemParameter
                = Expression.Parameter(
                    innerShaper.Type,
                    additionalFromClause.ItemName);

            AddOrUpdateMapping(additionalFromClause, innerItemParameter);

            var transparentIdentifierType
                = CreateTransparentIdentifierType(
                    CurrentParameter.Type,
                    innerItemParameter.Type);

            var materializer
                = Expression.Lambda(
                    CallCreateTransparentIdentifier(
                        transparentIdentifierType,
                        CurrentParameter,
                        innerItemParameter),
                    CurrentParameter,
                    innerItemParameter).Compile();

            var compositeShaper
                = CompositeShaper.Create(additionalFromClause, outerShaper, innerShaper, materializer);

            IntroduceTransparentScope(additionalFromClause, queryModel, index, transparentIdentifierType);

            compositeShaper.SaveAccessorExpression(QueryCompilationContext.QuerySourceMapping);

            innerShaper.UpdateQuerySource(additionalFromClause);

            Expression
                = Expression.Call(
                    outerShapedQuery.Method
                        .GetGenericMethodDefinition()
                        .MakeGenericMethod(transparentIdentifierType),
                    outerShapedQuery.Arguments[0],
                    outerShapedQuery.Arguments[1],
                    Expression.Constant(compositeShaper));

            return true;
        }

        private bool TryFlattenGroupJoin(
            GroupJoinClause groupJoinClause,
            QueryModel queryModel,
            int index,
            int previousProjectionCount)
        {
            if (RequiresClientJoin || RequiresClientSelectMany)
            {
                return false;
            }

            var groupJoinMethodCallExpression = Expression as MethodCallExpression;

            var outerShapedQuery
                = groupJoinMethodCallExpression?.Arguments.FirstOrDefault() as MethodCallExpression;

            var innerShapedQuery
                = groupJoinMethodCallExpression?.Arguments.Skip(1).FirstOrDefault() as MethodCallExpression;

            if (groupJoinMethodCallExpression == null
                || !groupJoinMethodCallExpression.Method.MethodIsClosedFormOf(LinqOperatorProvider.GroupJoin)
                || !IsShapedQueryExpression(outerShapedQuery)
                || !IsShapedQueryExpression(innerShapedQuery))
            {
                return false;
            }

            var joinClause = groupJoinClause.JoinClause;

            var outerQuerySource = FindPreviousQuerySource(queryModel, index);
            var outerSelectExpression = TryGetQuery(outerQuerySource);
            var innerSelectExpression = TryGetQuery(joinClause);

            if (outerSelectExpression == null || innerSelectExpression == null)
            {
                return false;
            }

            var sqlTranslatingExpressionVisitor
                = _sqlTranslatingExpressionVisitorFactory.Create(this);

            var predicate
                = sqlTranslatingExpressionVisitor.Visit(
                    Expression.Equal(joinClause.OuterKeySelector, joinClause.InnerKeySelector));

            if (predicate == null)
            {
                return false;
            }

            if (innerSelectExpression.Predicate != null)
            {
                var subSelectExpression = innerSelectExpression.PushDownSubquery();
                innerSelectExpression.ExplodeStarProjection();
                subSelectExpression.ClearProjection();
                subSelectExpression.IsProjectStar = true;
                subSelectExpression.QuerySource = joinClause;

                predicate 
                    = sqlTranslatingExpressionVisitor.Visit(
                        Expression.Equal(joinClause.OuterKeySelector, joinClause.InnerKeySelector));
            }

            QueriesBySource.Remove(joinClause);

            outerSelectExpression.RemoveRangeFromProjection(previousProjectionCount);

            var projection
                = QueryCompilationContext.QuerySourceRequiresMaterialization(joinClause)
                    ? innerSelectExpression.Projection
                    : Enumerable.Empty<Expression>();

            var joinExpression
                = outerSelectExpression.AddLeftOuterJoin(
                    innerSelectExpression.Tables.Single(),
                    projection);

            joinExpression.Predicate = predicate;
            joinExpression.QuerySource = joinClause;

            var outerJoinOrderingExtractor = new OuterJoinOrderingExtractor();
            outerJoinOrderingExtractor.Visit(predicate);

            if (!outerJoinOrderingExtractor.DependentToPrincipalFound)
            {
                foreach (var expression in outerJoinOrderingExtractor.Expressions)
                {
                    outerSelectExpression.AddToOrderBy(
                        new Ordering(expression, OrderingDirection.Asc));
                }
            }
            
            var outerShaper = ExtractShaper(outerShapedQuery, 0);
            var innerShaper = ExtractShaper(innerShapedQuery, previousProjectionCount);

            innerShaper.UpdateQuerySource(joinClause);

            var queryMethodProvider = QueryCompilationContext.QueryMethodProvider;

            var groupJoinMethod
                = queryMethodProvider.GroupJoinMethod.MakeGenericMethod(
                    outerShaper.Type,
                    innerShaper.Type,
                    ((LambdaExpression)groupJoinMethodCallExpression.Arguments[2]).ReturnType,
                    ((LambdaExpression)groupJoinMethodCallExpression.Arguments.Last()).ReturnType);

            var newShapedQueryMethod
                = Expression.Call(
                    queryMethodProvider.QueryMethod,
                    outerShapedQuery.Arguments[0],
                    outerShapedQuery.Arguments[1],
                    Expression.Default(typeof(int?)));

            var defaultGroupJoinInclude
                = Expression.Default(
                    queryMethodProvider.GroupJoinIncludeType);

            Expression =
                Expression.Call(
                    groupJoinMethod,
                    Expression.Convert(
                        QueryContextParameter,
                        typeof(RelationalQueryContext)),
                    newShapedQueryMethod,
                    Expression.Constant(outerShaper),
                    Expression.Constant(innerShaper),
                    groupJoinMethodCallExpression.Arguments[3],
                    groupJoinMethodCallExpression.Arguments[4],
                    defaultGroupJoinInclude,
                    defaultGroupJoinInclude);

            return true;
        }

        #endregion

        #region Binding

        /// <summary>
        ///     Bind a member expression to a value buffer access.
        /// </summary>
        /// <param name="memberExpression"> The member access expression. </param>
        /// <param name="expression"> The target expression. </param>
        /// <returns>
        ///     An Expression.
        /// </returns>
        public override Expression BindMemberToValueBuffer(MemberExpression memberExpression, Expression expression)
        {
            Check.NotNull(memberExpression, nameof(memberExpression));
            Check.NotNull(expression, nameof(expression));

            return BindMemberExpression(
                memberExpression,
                (property, querySource, selectExpression) =>
                    {
                        var projectionIndex = selectExpression.GetProjectionIndex(property, querySource);

                        Debug.Assert(projectionIndex > -1);

                        return BindReadValueMethod(memberExpression.Type, expression, projectionIndex);
                    },
                bindSubQueries: true);
        }

        /// <summary>
        ///     Bind a method call expression to a value buffer access.
        /// </summary>
        /// <param name="methodCallExpression"> The method call expression. </param>
        /// <param name="expression"> The target expression. </param>
        /// <returns>
        ///     An Expression.
        /// </returns>
        public override Expression BindMethodCallToValueBuffer(
            MethodCallExpression methodCallExpression, Expression expression)
        {
            Check.NotNull(methodCallExpression, nameof(methodCallExpression));
            Check.NotNull(expression, nameof(expression));

            return BindMethodCallExpression(
                       methodCallExpression,
                       (property, querySource, selectExpression) =>
                           {
                               var projectionIndex = selectExpression.GetProjectionIndex(property, querySource);

                               Debug.Assert(projectionIndex > -1);

                               return BindReadValueMethod(methodCallExpression.Type, expression, projectionIndex);
                           },
                       bindSubQueries: true)
                   ?? ParentQueryModelVisitor?
                       .BindMethodCallToValueBuffer(methodCallExpression, expression);
        }

        /// <summary>
        ///     Bind a member expression.
        /// </summary>
        /// <typeparam name="TResult"> Type of the result. </typeparam>
        /// <param name="memberExpression"> The member access expression. </param>
        /// <param name="memberBinder"> The member binder. </param>
        /// <param name="bindSubQueries"> true to bind sub queries. </param>
        /// <returns>
        ///     A TResult.
        /// </returns>
        public virtual TResult BindMemberExpression<TResult>(
            [NotNull] MemberExpression memberExpression,
            [NotNull] Func<IProperty, IQuerySource, SelectExpression, TResult> memberBinder,
            bool bindSubQueries = false)
        {
            Check.NotNull(memberExpression, nameof(memberExpression));
            Check.NotNull(memberBinder, nameof(memberBinder));

            return BindMemberExpression(memberExpression, null, memberBinder, bindSubQueries);
        }

        private TResult BindMemberExpression<TResult>(
            [NotNull] MemberExpression memberExpression,
            [CanBeNull] IQuerySource querySource,
            Func<IProperty, IQuerySource, SelectExpression, TResult> memberBinder,
            bool bindSubQueries)
        {
            Check.NotNull(memberExpression, nameof(memberExpression));
            Check.NotNull(memberBinder, nameof(memberBinder));

            return base.BindMemberExpression(memberExpression, querySource,
                (property, qs) => BindMemberOrMethod(memberBinder, qs, property, bindSubQueries));
        }

        public virtual Expression BindMemberToOuterQueryParameter(
            [NotNull] MemberExpression memberExpression)
            => base.BindMemberExpression(
                memberExpression,
                null,
                (property, qs) => BindPropertyToOuterParameter(qs, property, true));

        /// <summary>
        ///     Bind a method call expression.
        /// </summary>
        /// <typeparam name="TResult"> Type of the result. </typeparam>
        /// <param name="methodCallExpression"> The method call expression. </param>
        /// <param name="memberBinder"> The member binder. </param>
        /// <param name="bindSubQueries"> true to bind sub queries. </param>
        /// <returns>
        ///     A TResult.
        /// </returns>
        public virtual TResult BindMethodCallExpression<TResult>(
            [NotNull] MethodCallExpression methodCallExpression,
            [NotNull] Func<IProperty, IQuerySource, SelectExpression, TResult> memberBinder,
            bool bindSubQueries = false)
        {
            Check.NotNull(methodCallExpression, nameof(methodCallExpression));
            Check.NotNull(memberBinder, nameof(memberBinder));

            return BindMethodCallExpression(methodCallExpression, null, memberBinder, bindSubQueries);
        }

        private TResult BindMethodCallExpression<TResult>(
            MethodCallExpression methodCallExpression,
            IQuerySource querySource,
            Func<IProperty, IQuerySource, SelectExpression, TResult> memberBinder,
            bool bindSubQueries)
            => base.BindMethodCallExpression(
                methodCallExpression,
                querySource,
                (property, qs) => BindMemberOrMethod(memberBinder, qs, property, bindSubQueries));

        /// <summary>
        ///     Bind a local method call expression.
        /// </summary>
        /// <param name="methodCallExpression"> The local method call expression. </param>
        /// <returns>
        ///     An Expression.
        /// </returns>
        public virtual Expression BindLocalMethodCallExpression(
            [NotNull] MethodCallExpression methodCallExpression)
        {
            Check.NotNull(methodCallExpression, nameof(methodCallExpression));

            return base.BindMethodCallExpression<Expression>(methodCallExpression, null,
                (property, qs) =>
                    {
                        var parameterExpression = methodCallExpression.Arguments[0] as ParameterExpression;

                        if (parameterExpression != null)
                        {
                            return new PropertyParameterExpression(parameterExpression.Name, property);
                        }

                        var constantExpression = methodCallExpression.Arguments[0] as ConstantExpression;

                        if (constantExpression != null)
                        {
                            return Expression.Constant(
                                property.GetGetter().GetClrValue(constantExpression.Value),
                                methodCallExpression.Method.GetGenericArguments()[0]);
                        }

                        return null;
                    });
        }

        public virtual Expression BindMethodToOuterQueryParameter(
            [NotNull] MethodCallExpression methodCallExpression)
        {
            Check.NotNull(methodCallExpression, nameof(methodCallExpression));

            return base.BindMethodCallExpression<Expression>(
                methodCallExpression,
                null,
                (property, qs) => BindPropertyToOuterParameter(qs, property, false));
        }

        private TResult BindMemberOrMethod<TResult>(
            Func<IProperty, IQuerySource, SelectExpression, TResult> memberBinder,
            IQuerySource querySource,
            IProperty property,
            bool bindSubQueries)
        {
            if (querySource != null)
            {
                var selectExpression = TryGetQuery(querySource);

                if (selectExpression == null
                    && bindSubQueries)
                {
                    RelationalQueryModelVisitor subQueryModelVisitor;
                    if (_subQueryModelVisitorsBySource.TryGetValue(querySource, out subQueryModelVisitor))
                    {
                        if (!subQueryModelVisitor.RequiresClientProjection)
                        {
                            selectExpression = subQueryModelVisitor.Queries.SingleOrDefault();

                            selectExpression?
                                .AddToProjection(
                                    _relationalAnnotationProvider.For(property).ColumnName,
                                    property,
                                    querySource);
                        }
                    }
                }

                if (selectExpression != null)
                {
                    return memberBinder(property, querySource, selectExpression);
                }

                selectExpression
                    = ParentQueryModelVisitor?.TryGetQuery(querySource);

                selectExpression?.AddToProjection(
                    _relationalAnnotationProvider.For(property).ColumnName,
                    property,
                    querySource);
            }

            return default(TResult);
        }

        #endregion

        private bool _canBindPropertyToOuterParameter = true;

        private const string OuterQueryParameterNamePrefix = @"_outer_";

        private readonly Dictionary<string, Expression> _injectedParameters = new Dictionary<string, Expression>();

        private ParameterExpression BindPropertyToOuterParameter(IQuerySource querySource, IProperty property, bool isMemberExpression)
        {
            if (querySource != null && _canBindPropertyToOuterParameter)
            {
                var outerQueryModelVisitor = ParentQueryModelVisitor;
                var outerSelectExpression = outerQueryModelVisitor?.TryGetQuery(querySource);

                while (outerSelectExpression == null && outerQueryModelVisitor != null)
                {
                    outerQueryModelVisitor = outerQueryModelVisitor.ParentQueryModelVisitor;
                    outerSelectExpression = outerQueryModelVisitor?.TryGetQuery(querySource);
                }

                if (outerSelectExpression != null)
                {
                    var parameterName = OuterQueryParameterNamePrefix + property.Name;
                    var parameterWithSamePrefixCount
                        = QueryCompilationContext.ParentQueryReferenceParameters.Count(p => p.StartsWith(parameterName, StringComparison.Ordinal));

                    if (parameterWithSamePrefixCount > 0)
                    {
                        parameterName += parameterWithSamePrefixCount;
                    }

                    QueryCompilationContext.ParentQueryReferenceParameters.Add(parameterName);
                    
                    var querySourceReference = new QuerySourceReferenceExpression(querySource);
                    var propertyExpression = isMemberExpression
                        ? Expression.Property(querySourceReference, property.PropertyInfo)
                        : CreatePropertyExpression(querySourceReference, property);

                    if (propertyExpression.Type.GetTypeInfo().IsValueType)
                    {
                        propertyExpression = Expression.Convert(propertyExpression, typeof(object));
                    }

                    _injectedParameters[parameterName] = propertyExpression;

                    Expression 
                        = CreateInjectParametersExpression(
                            Expression, 
                            new Dictionary<string, Expression> { [parameterName] = propertyExpression });

                    return Expression.Parameter(
                        property.ClrType,
                        parameterName);
                }
            }

            return null;
        }

        private Expression CreateInjectParametersExpression(Expression expression, Dictionary<string, Expression> parameters)
        {
            var parameterNameExpressions = new List<ConstantExpression>();
            var parameterValueExpressions = new List<Expression>();

            if (expression is MethodCallExpression methodCallExpression
                && methodCallExpression.Method.MethodIsClosedFormOf(QueryCompilationContext.QueryMethodProvider.InjectParametersMethod))
            {
                var existingParameterNamesExpression = (NewArrayExpression)methodCallExpression.Arguments[2];
                parameterNameExpressions.AddRange(existingParameterNamesExpression.Expressions.Cast<ConstantExpression>());

                var existingParameterValuesExpression = (NewArrayExpression)methodCallExpression.Arguments[3];
                parameterValueExpressions.AddRange(existingParameterValuesExpression.Expressions);

                expression = methodCallExpression.Arguments[1];
            }

            parameterNameExpressions.AddRange(parameters.Keys.Select(k => Expression.Constant(k)));
            parameterValueExpressions.AddRange(parameters.Values);

            var elementType = expression.Type.GetTypeInfo().GenericTypeArguments.Single();

            return Expression.Call(
                QueryCompilationContext.QueryMethodProvider.InjectParametersMethod.MakeGenericMethod(elementType),
                QueryContextParameter,
                expression,
                Expression.NewArrayInit(typeof(string), parameterNameExpressions),
                Expression.NewArrayInit(typeof(object), parameterValueExpressions));
        }

        /// <summary>
        ///     Lifts the outer parameters injected into a subquery into the query
        ///     expression that is being built by this query model visitor, so that
        ///     the subquery can be lifted.
        /// </summary>
        /// <param name="subQueryModelVisitor"> The query model visitor for the subquery being lifted. </param>
        public virtual void LiftInjectedParameters([NotNull] RelationalQueryModelVisitor subQueryModelVisitor)
        {
            Check.NotNull(subQueryModelVisitor, nameof(subQueryModelVisitor));

            if (!subQueryModelVisitor._injectedParameters.Any())
            {
                return;
            }

            foreach (var pair in subQueryModelVisitor._injectedParameters)
            {
                _injectedParameters[pair.Key] = pair.Value;
            }

            Expression = CreateInjectParametersExpression(Expression, subQueryModelVisitor._injectedParameters);
        }
    }
}
