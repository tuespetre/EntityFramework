// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using Microsoft.EntityFrameworkCore.Specification.Tests;

namespace Microsoft.EntityFrameworkCore.Sqlite.FunctionalTests
{
    public class ComplexNavigationsQuerySqliteTest : ComplexNavigationsQueryTestBase<SqliteTestStore, ComplexNavigationsQuerySqliteFixture>
    {
        public ComplexNavigationsQuerySqliteTest(ComplexNavigationsQuerySqliteFixture fixture)
            : base(fixture)
        {
        }

        public override void GroupJoin_with_subquery_on_inner()
        {
            base.GroupJoin_with_subquery_on_inner();
        }

        public override void SelectMany_with_navigation_filter_paging_and_explicit_DefaultIfEmpty()
        {
            base.SelectMany_with_navigation_filter_paging_and_explicit_DefaultIfEmpty();
        }
    }
}
