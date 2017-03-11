// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore.Query.ExpressionVisitors.Internal;
using Microsoft.EntityFrameworkCore.Query.ResultOperators;
using Remotion.Linq;
using Remotion.Linq.Clauses;
using Remotion.Linq.Clauses.Expressions;
using Remotion.Linq.Clauses.ExpressionVisitors;
using Remotion.Linq.Clauses.ResultOperators;

namespace Microsoft.EntityFrameworkCore.Query.Internal
{
    /// <summary>
    ///     This API supports the Entity Framework Core infrastructure and is not intended to be used
    ///     directly from your code. This API may change or be removed in future releases.
    /// </summary>
    public class CrossJoinLateralClauseQueryModelVisitor : QueryModelVisitorBase
    {
        private readonly IEnumerable<IQueryAnnotation> _queryAnnotations;

        /// <summary>
        ///     This API supports the Entity Framework Core infrastructure and is not intended to be used
        ///     directly from your code. This API may change or be removed in future releases.
        /// </summary>
        public CrossJoinLateralClauseQueryModelVisitor([NotNull] IEnumerable<IQueryAnnotation> queryAnnotations)
        {
            _queryAnnotations = queryAnnotations;
        }

        /// <summary>
        ///     This API supports the Entity Framework Core infrastructure and is not intended to be used
        ///     directly from your code. This API may change or be removed in future releases.
        /// </summary>
        public override void VisitQueryModel([NotNull] QueryModel queryModel)
        {
            queryModel.TransformExpressions(new TransformingQueryModelExpressionVisitor<CrossJoinLateralClauseQueryModelVisitor>(this).Visit);

            for (var i = 0; i < queryModel.BodyClauses.Count; i++)
            {
                if (queryModel.BodyClauses[i] is AdditionalFromClause additionalFromClause)
                {
                    VisitAdditionalFromClause(additionalFromClause, queryModel, i);
                }
            }
        }

        /// <summary>
        ///     This API supports the Entity Framework Core infrastructure and is not intended to be used
        ///     directly from your code. This API may change or be removed in future releases.
        /// </summary>
        public override void VisitAdditionalFromClause(
            [NotNull] AdditionalFromClause additionalFromClause,
            [NotNull] QueryModel queryModel, 
            int index)
        {
            if (additionalFromClause.FromExpression is SubQueryExpression subQueryExpression)
            {
                if (index > 0
                    && subQueryExpression.QueryModel.MainFromClause.FromExpression is QuerySourceReferenceExpression qsre
                    && qsre.ReferencedQuerySource is GroupJoinClause groupJoinClause
                    && queryModel.CountQuerySourceReferences(groupJoinClause) == 1
                    && subQueryExpression.QueryModel.ResultOperators.Any(r => r is SkipResultOperator || r is TakeResultOperator))
                {
                    subQueryExpression.QueryModel.MainFromClause.FromExpression = groupJoinClause.JoinClause.InnerSequence;
                    subQueryExpression.QueryModel.MainFromClause.ItemName = groupJoinClause.JoinClause.ItemName;

                    var whereClauseMapping = new QuerySourceMapping();
                    whereClauseMapping.AddMapping(groupJoinClause.JoinClause,
                        new QuerySourceReferenceExpression(subQueryExpression.QueryModel.MainFromClause));

                    var whereClausePredicate
                        = ReferenceReplacingExpressionVisitor.ReplaceClauseReferences(
                            Expression.Equal(
                                groupJoinClause.JoinClause.OuterKeySelector, 
                                groupJoinClause.JoinClause.InnerKeySelector),
                            whereClauseMapping,
                            throwOnUnmappedReferences: false);

                    subQueryExpression.QueryModel.BodyClauses.Insert(0, new WhereClause(whereClausePredicate));
                    
                    queryModel.BodyClauses.Insert(index - 1, new CrossJoinLateralClause(additionalFromClause));
                    queryModel.BodyClauses.Remove(groupJoinClause);
                    queryModel.BodyClauses.Remove(additionalFromClause);

                    return;
                }

                var correlationFinder = new CorrelationFindingQueryModelVisitor();
                correlationFinder.VisitQueryModel(subQueryExpression.QueryModel);

                if (correlationFinder.DetectedCorrelation)
                {
                    queryModel.BodyClauses.Insert(index, new CrossJoinLateralClause(additionalFromClause));
                    queryModel.BodyClauses.Remove(additionalFromClause);
                }
            }
        }
    }
}
