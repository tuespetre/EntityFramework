// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Linq.Expressions;
using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore.Query.Expressions;
using Microsoft.EntityFrameworkCore.Utilities;

namespace Microsoft.EntityFrameworkCore.Query.ExpressionVisitors.Internal
{
    /// <summary>
    ///     This API supports the Entity Framework Core infrastructure and is not intended to be used
    ///     directly from your code. This API may change or be removed in future releases.
    /// </summary>
    public class ResultTransformingExpressionVisitor : ExpressionVisitorBase
    {
        private readonly RelationalQueryCompilationContext _relationalQueryCompilationContext;
        private readonly Type _resultType;

        /// <summary>
        ///     This API supports the Entity Framework Core infrastructure and is not intended to be used
        ///     directly from your code. This API may change or be removed in future releases.
        /// </summary>
        public ResultTransformingExpressionVisitor(
            [NotNull] RelationalQueryCompilationContext relationalQueryCompilationContext,
            [NotNull] Type resultType)
        {
            Check.NotNull(relationalQueryCompilationContext, nameof(relationalQueryCompilationContext));
            Check.NotNull(resultType, nameof(resultType));

            _relationalQueryCompilationContext = relationalQueryCompilationContext;
            _resultType = resultType;
        }

        /// <summary>
        ///     This API supports the Entity Framework Core infrastructure and is not intended to be used
        ///     directly from your code. This API may change or be removed in future releases.
        /// </summary>
        protected override Expression VisitExtension(Expression extensionExpression)
        {
            Check.NotNull(extensionExpression, nameof(extensionExpression));

            switch (extensionExpression)
            {
                case ShapedQueryExpression shapedQueryExpression:

                    return ResultOperatorHandler.CallWithPossibleCancellationToken(
                        _relationalQueryCompilationContext.QueryMethodProvider.GetResultMethod
                            .MakeGenericMethod(_resultType),
                        Expression.Call(
                            _relationalQueryCompilationContext.QueryMethodProvider.QueryMethod,
                            EntityQueryModelVisitor.QueryContextParameter,
                            Expression.Constant(shapedQueryExpression.ShaperCommandContext),
                            Expression.Default(typeof(int?))));

                case InjectParametersExpression injectParametersExpression
                when injectParametersExpression.QueryExpression is ShapedQueryExpression shapedQueryExpression:

                    return ResultOperatorHandler.CallWithPossibleCancellationToken(
                        _relationalQueryCompilationContext.QueryMethodProvider.GetResultMethod
                            .MakeGenericMethod(_resultType),
                        new InjectParametersExpression(
                            injectParametersExpression.QueryCompilationContext,
                            Expression.Call(
                                _relationalQueryCompilationContext.QueryMethodProvider.QueryMethod,
                                EntityQueryModelVisitor.QueryContextParameter,
                                Expression.Constant(shapedQueryExpression.ShaperCommandContext),
                                Expression.Default(typeof(int?))),
                            injectParametersExpression.Parameters));
            }

            return base.VisitExtension(extensionExpression);
        }

        /// <summary>
        ///     This API supports the Entity Framework Core infrastructure and is not intended to be used
        ///     directly from your code. This API may change or be removed in future releases.
        /// </summary>
        protected override Expression VisitMethodCall(MethodCallExpression methodCallExpression)
        {
            Check.NotNull(methodCallExpression, nameof(methodCallExpression));

            // ReSharper disable once LoopCanBePartlyConvertedToQuery
            foreach (var argument in methodCallExpression.Arguments)
            {
                var newArgument = Visit(argument);

                if (newArgument != argument)
                {
                    return newArgument;
                }
            }

            return methodCallExpression;
        }
    }
}
