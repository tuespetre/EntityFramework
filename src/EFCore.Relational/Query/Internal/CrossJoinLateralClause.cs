// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore.Utilities;
using Remotion.Linq;
using Remotion.Linq.Clauses;

namespace Microsoft.EntityFrameworkCore.Query.Internal
{
    /// <summary>
    ///     This API supports the Entity Framework Core infrastructure and is not intended to be used
    ///     directly from your code. This API may change or be removed in future releases.
    /// </summary>
    public sealed class CrossJoinLateralClause : ICompositeBodyClause, IQuerySource
    {
        public CrossJoinLateralClause(
            [NotNull] AdditionalFromClause additionalFromClause)
        {
            AdditionalFromClause = additionalFromClause ?? throw new ArgumentNullException(nameof(additionalFromClause));
        }

        public AdditionalFromClause AdditionalFromClause { get; }

        public string ItemName => AdditionalFromClause.ItemName;

        public Type ItemType => AdditionalFromClause.ItemType;

        public IEnumerable<IBodyClause> BodyClauses
        {
            get
            {
                yield return AdditionalFromClause;
            }
        }

        public void Accept([NotNull] IQueryModelVisitor visitor, [NotNull] QueryModel queryModel, int index)
        {
            Check.NotNull(visitor, nameof(visitor));
            Check.NotNull(queryModel, nameof(queryModel));

            if (visitor is RelationalQueryModelVisitor relationalQueryModelVisitor)
            {
                relationalQueryModelVisitor.VisitCrossJoinLateralClause(this, queryModel, index);
            }
            else
            {
                visitor.VisitAdditionalFromClause(AdditionalFromClause, queryModel, index);
            }
        }

        public IBodyClause Clone([NotNull] CloneContext cloneContext)
        {
            return new CrossJoinLateralClause(
                AdditionalFromClause.Clone(cloneContext));
        }

        public void TransformExpressions([NotNull] Func<Expression, Expression> transformation)
        {
            AdditionalFromClause.TransformExpressions(transformation);
        }
    }
}
