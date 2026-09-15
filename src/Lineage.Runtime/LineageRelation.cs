namespace Lineage
{
    /// <summary>
    /// A causal edge stored separately from steps. ChildStepId is the produced value;
    /// ParentStepId is one value that contributed to it.
    /// </summary>
    public struct LineageRelation
    {
        public int ChildStepId;
        public int ParentStepId;

        public LineageRelation(int childStepId, int parentStepId)
        {
            ChildStepId = childStepId;
            ParentStepId = parentStepId;
        }
    }
}
