// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore.Utilities;

namespace Microsoft.EntityFrameworkCore.Query.Expressions
{
    /// <summary>
    ///     Reducible annotation expression representing a call to the InjectParameters
    ///     query method.
    /// </summary>
    public sealed class InjectParametersExpression : Expression
    {
        /// <summary>
        ///     Creates an instance of <see cref="InjectParametersExpression"/>.
        /// </summary>
        /// <param name="queryCompilationContext"> The query compilation context. </param>
        /// <param name="queryExpression"> The query expression. </param>
        /// <param name="parameters"> The parameters. </param>
        public InjectParametersExpression(
            [NotNull] RelationalQueryCompilationContext queryCompilationContext,
            [NotNull] Expression queryExpression,
            [NotNull] IReadOnlyDictionary<string, Expression> parameters)
        {
            Check.NotNull(queryCompilationContext, nameof(queryCompilationContext));
            Check.NotNull(queryExpression, nameof(queryExpression));
            Check.NotNull(parameters, nameof(parameters));

            QueryCompilationContext = queryCompilationContext;

            if (queryExpression is InjectParametersExpression injectParametersExpression)
            {
                var combinedParameters = new Dictionary<string, Expression>();

                foreach (var pair in injectParametersExpression.Parameters)
                {
                    combinedParameters[pair.Key] = pair.Value;
                }

                foreach (var pair in parameters)
                {
                    combinedParameters[pair.Key] = pair.Value;
                }

                QueryExpression = injectParametersExpression.QueryExpression;
                Parameters = combinedParameters;
            }
            else
            {
                QueryCompilationContext = queryCompilationContext;
                QueryExpression = queryExpression;
                Parameters = parameters;
            }
        }

        /// <summary>
        ///     The query compilation context.
        /// </summary>
        public RelationalQueryCompilationContext QueryCompilationContext { get; }

        /// <summary>
        ///     The query expression.
        /// </summary>
        public Expression QueryExpression { get; }

        /// <summary>
        ///     The parameter names.
        /// </summary>
        public IReadOnlyDictionary<string, Expression> Parameters { get; }

        /// <summary>
        ///     The type.
        /// </summary>
        public override Type Type => QueryExpression.Type;

        /// <summary>
        ///     Type of the node.
        /// </summary>
        public override ExpressionType NodeType => ExpressionType.Extension;

        /// <summary>
        ///     Indicates that the node can be reduced to a simpler node. If this returns true, Reduce() can be called to produce the reduced
        ///     form.
        /// </summary>
        /// <returns>True if the node can be reduced, otherwise false.</returns>
        public override bool CanReduce => true;

        /// <summary>
        ///     Reduces this node to a simpler expression. If CanReduce returns true, this should return a valid expression. This method can
        ///     return another node which itself must be reduced.
        /// </summary>
        /// <returns>The reduced expression.</returns>
        public override Expression Reduce()
            => Call(
                QueryCompilationContext.QueryMethodProvider.InjectParametersMethod
                    .MakeGenericMethod(QueryExpression.Type.GetSequenceType()),
                EntityQueryModelVisitor.QueryContextParameter,
                QueryExpression,
                NewArrayInit(typeof(string), Parameters.Keys.Select(Constant)),
                NewArrayInit(typeof(object), Parameters.Values));


        /// <summary>
        ///     Reduces the node and then calls the visitor delegate on the reduced expression. The method throws an exception if the node is not
        ///     reducible.
        /// </summary>
        /// <returns>The expression being visited, or an expression which should replace it in the tree.</returns>
        /// <param name="visitor">An instance of <see cref="T:System.Func`2" />.</param>
        protected override Expression VisitChildren(ExpressionVisitor visitor)
        {
            var queryExpression = visitor.Visit(QueryExpression);

            var parameters = new Dictionary<string, Expression>();
            var parametersChanged = false;

            foreach (var pair in Parameters)
            {
                var value = visitor.Visit(pair.Value);
                parameters[pair.Key] = value;
                parametersChanged |= value != pair.Value;
            }

            return queryExpression != QueryExpression || parametersChanged
                ? new InjectParametersExpression(
                    QueryCompilationContext,
                    queryExpression,
                    parameters)
                : this;
        }
    }
}
