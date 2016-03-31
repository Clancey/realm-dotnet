/* Copyright 2015 Realm Inc - All Rights Reserved
 * Proprietary and Confidential
 */
 
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Runtime.CompilerServices;

namespace Realms
{
    internal class RealmResultsVisitor : ExpressionVisitor
    {
        private Realm _realm;
        internal QueryHandle _coreQueryHandle;  // set when recurse down to VisitConstant
        internal SortOrderHandle _optionalSortOrderHandle;  // set only when get OrderBy*
        private Type _retType;


        internal RealmResultsVisitor(Realm realm, Type retType)
        {
            _realm = realm;
            _retType = retType;
        }

        private static Expression StripQuotes(Expression e)
        {
            while (e.NodeType == ExpressionType.Quote)
            {
                e = ((UnaryExpression)e).Operand;
            }
            return e;
        }


        /**
        Expressions will typically be in a form:
        - with embedded Lambda `Count(p => !p.IsInteresting)`
        - at the end of a Where `Where(p => !p.IsInteresting).Where()`

        The latter form is handled by recursion where evaluation of Visit will 
        take us back into VisitMethodCall to evaluate the Where call.

        */
        private void RecurseToWhereOrRunLambda(MethodCallExpression m)
        {
            this.Visit(m.Arguments[0]);  // creates the query or recurse to "Where"
            if (m.Arguments.Count > 1) {
                LambdaExpression lambda = (LambdaExpression)StripQuotes (m.Arguments[1]);
                this.Visit (lambda.Body);
            }
        }


        private void AddSort(LambdaExpression lambda, bool isStarting, bool ascending)
        {
            var body = lambda.Body as MemberExpression;
            if (body == null)
                throw new NotSupportedException($"The expression {lambda} cannot be used in an Order clause");

            if (isStarting)
            {
                if (_optionalSortOrderHandle == null)
                    _optionalSortOrderHandle = _realm.MakeSortOrderForTable(_retType);
                else
                {
                    var badCall = ascending ? "By" : "ByDescending";
                    throw new NotSupportedException($"You can only use one OrderBy or OrderByDescending clause, subsequent sort conditions should be Then{badCall}");
                }
            }

            var sortColName = body.Member.Name;
            NativeSortOrder.add_sort_clause(_optionalSortOrderHandle, sortColName, (IntPtr)sortColName.Length, ascending ? (IntPtr)1 : IntPtr.Zero);
        }


        internal override Expression VisitMethodCall(MethodCallExpression m)
        {
            if (m.Method.DeclaringType == typeof(Queryable)) { 
                if (m.Method.Name == "Where")
                {
                    this.Visit(m.Arguments[0]);
                    LambdaExpression lambda = (LambdaExpression)StripQuotes(m.Arguments[1]);
                    this.Visit(lambda.Body);
                    return m;
                }
                if (m.Method.Name == "OrderBy")
                {
                    this.Visit(m.Arguments[0]);
                    AddSort((LambdaExpression)StripQuotes(m.Arguments[1]), true, true);
                    return m;
                }
                if (m.Method.Name == "OrderByDescending")
                {
                    this.Visit(m.Arguments[0]);
                    AddSort((LambdaExpression)StripQuotes(m.Arguments[1]), true, false);
                    return m;
                }
                if (m.Method.Name == "ThenBy")
                {
                    this.Visit(m.Arguments[0]);
                    AddSort((LambdaExpression)StripQuotes(m.Arguments[1]), false, true);
                    return m;
                }
                if (m.Method.Name == "ThenByDescending")
                {
                    this.Visit(m.Arguments[0]);
                    AddSort((LambdaExpression)StripQuotes(m.Arguments[1]), false, false);
                    return m;
                }
                if (m.Method.Name == "Count")
                {
                    RecurseToWhereOrRunLambda(m);  
                    int foundCount = (int)NativeQuery.count(_coreQueryHandle);
                    return Expression.Constant(foundCount);
                }
                if (m.Method.Name == "Any")
                {
                    RecurseToWhereOrRunLambda(m);  
                    RowHandle firstRow = NativeQuery.findDirect(_coreQueryHandle, IntPtr.Zero);
                    bool foundAny = !firstRow.IsInvalid;
                    return Expression.Constant(foundAny);
                }

                //TODO as per issue #360 fix this to use Results so get sorted First.
                if (m.Method.Name == "First")
                {
                    RecurseToWhereOrRunLambda(m);  
                    RowHandle firstRow = NativeQuery.findDirect(_coreQueryHandle, IntPtr.Zero);
                    if (firstRow.IsInvalid)
                        throw new InvalidOperationException("Sequence contains no matching element");
                    return Expression.Constant(_realm.MakeObjectForRow(_retType, firstRow));
                }
                if (m.Method.Name == "Single")  // same as First with extra checks
                {
                    RecurseToWhereOrRunLambda(m);  
                    RowHandle firstRow = NativeQuery.findDirect(_coreQueryHandle, IntPtr.Zero);
                    if (firstRow.IsInvalid)
                        throw new InvalidOperationException("Sequence contains no matching element");
                    IntPtr nextIndex = (IntPtr)(firstRow.RowIndex+1);
                    RowHandle nextRow = NativeQuery.findDirect(_coreQueryHandle, nextIndex);
                    if (!nextRow.IsInvalid)
                        throw new InvalidOperationException("Sequence contains more than one matching element");
                    return Expression.Constant(_realm.MakeObjectForRow(_retType, firstRow));
                }

            }
            throw new NotSupportedException($"The method '{m.Method.Name}' is not supported");
        }

        internal override Expression VisitUnary(UnaryExpression u)
        {
            switch (u.NodeType)
            {
            case ExpressionType.Not:
                {
                    NativeQuery.not(_coreQueryHandle);                        
                    this.Visit (u.Operand);  // recurse into richer expression, expect to VisitCombination
                }
                    break;
                default:
                    throw new NotSupportedException($"The unary operator '{u.NodeType}' is not supported");
            }
            return u;
        }

        protected void VisitCombination(BinaryExpression b,  Action<QueryHandle> combineWith )
        {
            NativeQuery.group_begin(_coreQueryHandle);
            Visit(b.Left);
            combineWith(_coreQueryHandle);
            Visit(b.Right);
            NativeQuery.group_end(_coreQueryHandle);
        }


        internal override Expression VisitBinary(BinaryExpression b)
        {
            if (b.NodeType == ExpressionType.AndAlso)  // Boolean And with short-circuit
            {
                VisitCombination(b, (qh) => { /* noop -- AND is the default combinator */} );
            }
            else if (b.NodeType == ExpressionType.OrElse)  // Boolean Or with short-circuit
            {
                VisitCombination(b, qh => NativeQuery.or(qh) );
            }
            else
            {
                var leftMember = b.Left as MemberExpression;
                if (leftMember == null)
                    throw new NotSupportedException(
                        $"The lhs of the binary operator '{b.NodeType}' should be a member expression");
                var leftName = leftMember.Member.Name;
                var rightConst = b.Right as ConstantExpression;
                if (rightConst == null)
                    throw new NotSupportedException(
                        $"The rhs of the binary operator '{b.NodeType}' should be a constant expression");

                var rightValue = rightConst.Value;
                switch (b.NodeType)
                {
                    case ExpressionType.Equal:
                        AddQueryEqual(_coreQueryHandle, leftName, rightValue);
                        break;

                    case ExpressionType.NotEqual:
                        AddQueryNotEqual(_coreQueryHandle, leftName, rightValue);
                        break;

                    case ExpressionType.LessThan:
                        AddQueryLessThan(_coreQueryHandle, leftName, rightValue);
                        break;

                    case ExpressionType.LessThanOrEqual:
                        AddQueryLessThanOrEqual(_coreQueryHandle, leftName, rightValue);
                        break;

                    case ExpressionType.GreaterThan:
                        AddQueryGreaterThan(_coreQueryHandle, leftName, rightValue);
                        break;

                    case ExpressionType.GreaterThanOrEqual:
                        AddQueryGreaterThanOrEqual(_coreQueryHandle, leftName, rightValue);
                        break;

                    default:
                        throw new NotSupportedException(string.Format("The binary operator '{0}' is not supported", b.NodeType));
                }
            }
            return b;
        }

#pragma warning disable 0642    // Disable warning about empty statements (See issue #68)

        private static void AddQueryEqual(QueryHandle queryHandle, string columnName, object value)
            {
            var columnIndex = NativeQuery.get_column_index((QueryHandle)queryHandle, columnName, (IntPtr)columnName.Length);

            var valueType = value.GetType();
            if (value is string)
            {
                string valueStr = (string)value;
                NativeQuery.string_equal((QueryHandle)queryHandle, columnIndex, valueStr, (IntPtr)valueStr.Length);
            }
            else if (valueType == typeof(bool))
                NativeQuery.bool_equal((QueryHandle)queryHandle, columnIndex, MarshalHelpers.BoolToIntPtr((bool)value));
            else if (valueType == typeof(int))
                NativeQuery.int_equal((QueryHandle)queryHandle, columnIndex, (IntPtr)((int)value));
            else if (valueType == typeof(float))
                NativeQuery.float_equal((QueryHandle)queryHandle, columnIndex, (float)value);
            else if (valueType == typeof(double))
                NativeQuery.double_equal((QueryHandle)queryHandle, columnIndex, (double)value);
            else
                throw new NotImplementedException();
        }

        private static void AddQueryNotEqual(QueryHandle queryHandle, string columnName, object value)
        {
            var columnIndex = NativeQuery.get_column_index((QueryHandle)queryHandle, columnName, (IntPtr)columnName.Length);

            var valueType = value.GetType();
            if (value.GetType() == typeof(string))
            {
                string valueStr = (string)value;
                NativeQuery.string_not_equal((QueryHandle)queryHandle, columnIndex, valueStr, (IntPtr)valueStr.Length);
            }
            else if (valueType == typeof(bool))
                NativeQuery.bool_not_equal((QueryHandle)queryHandle, columnIndex, MarshalHelpers.BoolToIntPtr((bool)value));
            else if (valueType == typeof(int))
                NativeQuery.int_not_equal((QueryHandle)queryHandle, columnIndex, (IntPtr)((int)value));
            else if (valueType == typeof(float))
                NativeQuery.float_not_equal((QueryHandle)queryHandle, columnIndex, (float)value);
            else if (valueType == typeof(double))
                NativeQuery.double_not_equal((QueryHandle)queryHandle, columnIndex, (double)value);
            else
                throw new NotImplementedException();
        }

        private static void AddQueryLessThan(QueryHandle queryHandle, string columnName, object value)
        {
            var columnIndex = NativeQuery.get_column_index((QueryHandle)queryHandle, columnName, (IntPtr)columnName.Length);

            var valueType = value.GetType();
            if (valueType == typeof(int))
                NativeQuery.int_less((QueryHandle)queryHandle, columnIndex, (IntPtr)((int)value));
            else if (valueType == typeof(float))
                NativeQuery.float_less((QueryHandle)queryHandle, columnIndex, (float)value);
            else if (valueType == typeof(double))
                NativeQuery.double_less((QueryHandle)queryHandle, columnIndex, (double)value);
            else if (valueType == typeof(string) || valueType == typeof(bool))
                throw new Exception("Unsupported type " + valueType.Name);
            else
                throw new NotImplementedException();
        }

        private static void AddQueryLessThanOrEqual(QueryHandle queryHandle, string columnName, object value)
        {
            var columnIndex = NativeQuery.get_column_index((QueryHandle)queryHandle, columnName, (IntPtr)columnName.Length);

            var valueType = value.GetType();
            if (valueType == typeof(int))
                NativeQuery.int_less_equal((QueryHandle)queryHandle, columnIndex, (IntPtr)((int)value));
            else if (valueType == typeof(float))
                NativeQuery.float_less_equal((QueryHandle)queryHandle, columnIndex, (float)value);
            else if (valueType == typeof(double))
                NativeQuery.double_less_equal((QueryHandle)queryHandle, columnIndex, (double)value);
            else if (valueType == typeof(string) || valueType == typeof(bool))
                throw new Exception("Unsupported type " + valueType.Name);
            else
                throw new NotImplementedException();
        }

        private static void AddQueryGreaterThan(QueryHandle queryHandle, string columnName, object value)
        {
            var columnIndex = NativeQuery.get_column_index((QueryHandle)queryHandle, columnName, (IntPtr)columnName.Length);

            var valueType = value.GetType();
            if (valueType == typeof(int))
                NativeQuery.int_greater((QueryHandle)queryHandle, columnIndex, (IntPtr)((int)value));
            else if (valueType == typeof(float))
                NativeQuery.float_greater((QueryHandle)queryHandle, columnIndex, (float)value);
            else if (valueType == typeof(double))
                NativeQuery.double_greater((QueryHandle)queryHandle, columnIndex, (double)value);
            else if (valueType == typeof(string) || valueType == typeof(bool))
                throw new Exception("Unsupported type " + valueType.Name);
            else
                throw new NotImplementedException();
        }

        private static void AddQueryGreaterThanOrEqual(QueryHandle queryHandle, string columnName, object value)
        {
            var columnIndex = NativeQuery.get_column_index((QueryHandle)queryHandle, columnName, (IntPtr)columnName.Length);

            var valueType = value.GetType();
            if (valueType == typeof(int))
                NativeQuery.int_greater_equal((QueryHandle)queryHandle, columnIndex, (IntPtr)((int)value));
            else if (valueType == typeof(float))
                NativeQuery.float_greater_equal((QueryHandle)queryHandle, columnIndex, (float)value);
            else if (valueType == typeof(double))
                NativeQuery.double_greater_equal((QueryHandle)queryHandle, columnIndex, (double)value);
            else if (valueType == typeof(string) || valueType == typeof(bool))
                throw new Exception("Unsupported type " + valueType.Name);
            else
                throw new NotImplementedException();
        }

#pragma warning restore 0642

        // strange as it may seem, this is also called for the LHS when simply iterating All<T>()
        internal override Expression VisitConstant(ConstantExpression c)
        {
            IQueryable q = c.Value as IQueryable;
            if (q != null)
            {
                // assume constant nodes w/ IQueryables are table references
                if (_coreQueryHandle != null)
                    throw new Exception("We already have a table...");

                _coreQueryHandle = CreateQuery(q.ElementType);
            }
            else if (c.Value == null)
            {
            }
            else
            {
                if (c.Value is bool)
                {
                } 
                else if (c.Value is string)
                {
                }
                else if (c.Value.GetType() == typeof (object))
                {
                    throw new NotSupportedException(string.Format("The constant for '{0}' is not supported", c.Value));
                }
                else
                {
                }
            }
            return c;
        }

        private QueryHandle CreateQuery(Type elementType)
        {
            var tableHandle = _realm._tableHandles[elementType];
            var queryHandle = tableHandle.TableWhere();

            //At this point sh is invalid due to its handle being uninitialized, but the root is set correctly
            //a finalize at this point will not leak anything and the handle will not do anything

            //now, set the TableView handle...
            RuntimeHelpers.PrepareConstrainedRegions();//the following finally will run with no out-of-band exceptions
            try { }
            finally
            {
                queryHandle.SetHandle(NativeTable.where(tableHandle));
            }//at this point we have atomically acquired a handle and also set the root correctly so it can be unbound correctly
            return queryHandle;
        }

        internal override Expression VisitMemberAccess(MemberExpression m)
        {
            if (m.Expression != null && m.Expression.NodeType == ExpressionType.Parameter)
            {
                if (m.Type == typeof(Boolean)) {
                    object rhs = true;  // box value
                    var leftName = m.Member.Name;
                    AddQueryEqual(_coreQueryHandle, leftName, rhs);
                }
                return m;
            }
            throw new NotSupportedException(string.Format("The member '{0}' is not supported", m.Member.Name));
        }
    }
}