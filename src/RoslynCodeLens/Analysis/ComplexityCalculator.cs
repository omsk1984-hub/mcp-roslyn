using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoslynCodeLens.Analysis;

public static class ComplexityCalculator
{
    /// <summary>
    /// Computes McCabe cyclomatic complexity for the given syntax node.
    /// Counts: <c>if</c>, each non-<c>default</c> switch label, each non-discard switch-expression
    /// arm, for/foreach/while/do, catch, conditional expression, and the short-circuit operators
    /// (&amp;&amp;, ||, ??).
    /// <para>
    /// <c>else</c> is deliberately NOT counted: it introduces no decision of its own, and an
    /// <c>else if</c> is already counted by its own <see cref="IfStatementSyntax"/>. Counting it
    /// previously made an if/else-if chain score roughly double.
    /// </para>
    /// </summary>
    public static int Calculate(SyntaxNode node) =>
        1 + CountDecisionNodes(node) + CountShortCircuitTokens(node);

    /// <summary>
    /// Decision nodes: every control construct plus the switch labels/arms, minus the discard arm.
    /// </summary>
    private static int CountDecisionNodes(SyntaxNode node)
    {
        var count = 0;

        foreach (var descendant in node.DescendantNodes())
        {
            if (IsDecisionNode(descendant))
                count++;
        }

        return count;
    }

    private static bool IsDecisionNode(SyntaxNode node) => node.Kind() switch
    {
        SyntaxKind.IfStatement
            or SyntaxKind.ForStatement
            or SyntaxKind.ForEachStatement
            or SyntaxKind.WhileStatement
            or SyntaxKind.DoStatement
            or SyntaxKind.CatchClause
            or SyntaxKind.ConditionalExpression
            // Count labels rather than sections: `case 1: case 2:` shares one section but is
            // two decisions, and `default:` (a DefaultSwitchLabelSyntax) is none.
            or SyntaxKind.CaseSwitchLabel
            or SyntaxKind.CasePatternSwitchLabel => true,

        // Switch *expressions* have no sections at all, so they used to be invisible.
        // A discard arm is the expression form of `default:` and is not a decision.
        SyntaxKind.SwitchExpressionArm => ((SwitchExpressionArmSyntax)node).Pattern is not DiscardPatternSyntax,

        _ => false,
    };

    /// <summary>Short-circuit operators, counted per token as McCabe defines them.</summary>
    private static int CountShortCircuitTokens(SyntaxNode node)
    {
        var count = 0;

        foreach (var token in node.DescendantTokens())
        {
#pragma warning disable EPS06
            var kind = token.Kind();
#pragma warning restore EPS06
            switch (kind)
            {
                case SyntaxKind.AmpersandAmpersandToken:
                case SyntaxKind.BarBarToken:
                case SyntaxKind.QuestionQuestionToken:
                    count++;
                    break;
            }
        }

        return count;
    }
}
