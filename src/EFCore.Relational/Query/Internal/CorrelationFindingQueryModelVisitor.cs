// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System.Collections.Generic;
using System.Linq.Expressions;
using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore.Query.ExpressionVisitors.Internal;
using Remotion.Linq;
using Remotion.Linq.Clauses;
using Remotion.Linq.Clauses.Expressions;

namespace Microsoft.EntityFrameworkCore.Query.Internal
{
    /// <summary>
    ///     This API supports the Entity Framework Core infrastructure and is not intended to be used
    ///     directly from your code. This API may change or be removed in future releases.
    /// </summary>
    public class CorrelationFindingQueryModelVisitor : QueryModelVisitorBase
    {
        private readonly HashSet<IQuerySource> _visitedQuerySources = new HashSet<IQuerySource>();

        /// <summary>
        ///     This API supports the Entity Framework Core infrastructure and is not intended to be used
        ///     directly from your code. This API may change or be removed in future releases.
        /// </summary>
        public virtual bool DetectedCorrelation { get; private set; }

        /// <summary>
        ///     This API supports the Entity Framework Core infrastructure and is not intended to be used
        ///     directly from your code. This API may change or be removed in future releases.
        /// </summary>
        public override void VisitQueryModel([NotNull] QueryModel queryModel)
        {
            var correlationFindingExpressionVisitor = new CorrelationFindingExpressionVisitor(this);

            _visitedQuerySources.Add(queryModel.MainFromClause);

            queryModel.MainFromClause.TransformExpressions(correlationFindingExpressionVisitor.Visit);

            foreach (var bodyClause in queryModel.BodyClauses)
            {
                if (bodyClause is IQuerySource querySourceBodyClause)
                {
                    _visitedQuerySources.Add(querySourceBodyClause);
                }

                bodyClause.TransformExpressions(correlationFindingExpressionVisitor.Visit);
            }

            queryModel.SelectClause.TransformExpressions(correlationFindingExpressionVisitor.Visit);

            foreach (var resultOperator in queryModel.ResultOperators)
            {
                if (resultOperator is IQuerySource querySourceResultOperator)
                {
                    _visitedQuerySources.Add(querySourceResultOperator);
                }

                resultOperator.TransformExpressions(correlationFindingExpressionVisitor.Visit);
            }
        }

        private class CorrelationFindingExpressionVisitor : TransformingQueryModelExpressionVisitor<CorrelationFindingQueryModelVisitor>
        {
            public CorrelationFindingExpressionVisitor(CorrelationFindingQueryModelVisitor queryModelVisitor)
                : base(queryModelVisitor)
            {
            }

            protected override Expression VisitQuerySourceReference(QuerySourceReferenceExpression expression)
            {
                if (!TransformingQueryModelVisitor._visitedQuerySources.Contains(expression.ReferencedQuerySource))
                {
                    TransformingQueryModelVisitor.DetectedCorrelation = true;
                }

                return base.VisitQuerySourceReference(expression);
            }
        }
    }
}
