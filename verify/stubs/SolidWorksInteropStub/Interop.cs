// VERIFY ONLY. Mirrors the shape of the SolidWorks primary interop assemblies for the members used by
// Mediator.SwxWorker. Enum values here are placeholders; code must use the enum names, never numbers.
#pragma warning disable IDE1006
namespace SolidWorks.Interop.sldworks
{
    public interface SldWorks
    {
        bool Visible { get; set; }
        bool CommandInProgress { get; set; }
        string RevisionNumber();
        int GetProcessID();
        object GetOpenDocumentByName(string DocumentName);
        ModelDoc2 OpenDoc6(string FileName, int Type, int Options, string Configuration, ref int Errors, ref int Warnings);
        object NewDocument(string TemplateName, int PaperSize, double Width, double Height);
        string GetUserPreferenceStringValue(int UserPreferenceValue);
        bool GetUserPreferenceToggle(int UserPreferenceValue);
        void SetUserPreferenceToggle(int UserPreferenceValue, bool OnFlag);
        int GetUserPreferenceIntegerValue(int UserPreferenceValue);
        bool SetUserPreferenceIntegerValue(int UserPreferenceValue, int Value);
        void CloseDoc(string Name);
        int GetDocumentCount();
        void ExitApp();
        /// <summary>Import options for a neutral file (STEP/IGES); pass to LoadFile4.</summary>
        object GetImportFileData(string FileName);
        /// <summary>Opens/imports a non-native file (e.g. STEP) as a new document.</summary>
        ModelDoc2 LoadFile4(string FileName, string ArgString, object ImportData, ref int Errors);
        /// <summary>The documents open in this session (object[] of ModelDoc2). VERIFY on the target version.</summary>
        object GetDocuments();
    }

    public interface ModelDoc2
    {
        ModelDocExtension Extension { get; }
        FeatureManager FeatureManager { get; }
        object ActiveView { get; }
        object SelectionManager { get; }
        string GetTitle();
        string GetPathName();
        int GetType();
        object GetConfigurationNames();
        bool ForceRebuild3(bool TopOnly);
        bool ClearSelection2(bool All);
        object FirstFeature();
        bool GetSaveFlag();
    }

    public interface ModelDocExtension
    {
        object GetPersistReference3(object Dispatch);
        object GetObjectByPersistReference3(object PersistReference, out int ErrorCode);
        bool StartRecordingUndoObject();
        bool FinishRecordingUndoObject2(string Name, bool Hidden);
        int GetUserPreferenceInteger(int UserPreferenceValue, int UserPreferenceOption);
        MassProperty CreateMassProperty();
        // V5: the obsolete SaveAs is deliberately absent so the worker cannot fall back to it. SaveAs3 needs SOLIDWORKS 2021 or later.
        bool SaveAs3(string Name, int Version, int Options, object ExportData, object AdvancedSaveAsOptions, ref int Errors, ref int Warnings);
    }

    public interface PartDoc
    {
        object GetBodies2(int BodyType, bool BVisibleOnly);
        object GetPartBox(bool Precise);
        bool SetEntityName(object Entity, string Name);
        string GetEntityName(object Entity);
        object GetEntityByName(string Name, int EntityType);
    }

    public interface AssemblyDoc
    {
        Component2 AddComponent5(string CompName, int ConfigOption, string NewConfigName, bool UseConfigForPartReferences,
            string ExistingConfigName, double X, double Y, double Z);
        void FixComponent();
        Mate2 AddMate5(int MateTypeFromEnum, int AlignFromEnum, bool Flip, double Distance, double DistanceAbsUpperLimit,
            double DistanceAbsLowerLimit, double GearRatioNumerator, double GearRatioDenominator, double Angle,
            double AngleAbsUpperLimit, double AngleAbsLowerLimit, bool ForPositioningOnly, bool LockRotation,
            int WidthMateOption, out int ErrorStatus);
    }

    public interface Component2
    {
        string Name2 { get; }
        object GetModelDoc2();
        object GetCorrespondingEntity(object Entity);
        bool Select4(bool Append, SelectData Data, bool ShowPopup);
        object FeatureByName(string Name);
    }

    public interface Body2 { object GetFaces(); }

    public interface Face2
    {
        object GetSurface();
        double GetArea();
        bool FaceInSurfaceSense();
    }

    /// <summary>
    /// CylinderParams: double[7] = origin X,Y,Z (m), axis I,J,K (unit), radius (m).
    /// PlaneParams:    double[6] = normal I,J,K (unit), root point X,Y,Z (m).
    /// </summary>
    public interface Surface
    {
        bool IsCylinder();
        bool IsPlane();
        object CylinderParams { get; }
        object PlaneParams { get; }
    }

    public interface Entity { bool Select4(bool Append, SelectData Data); }

    public interface Feature
    {
        string Name { get; set; }
        string GetTypeName2();
        object GetNextFeature();
        bool Select2(bool Append, int Mark);
        object GetFirstSubFeature();
        object GetNextSubFeature();
        int GetErrorCode2(out bool IsWarning);
    }

    public interface FeatureManager { bool EnableFeatureTree { get; set; } }
    public interface ModelView { bool EnableGraphicsUpdate { get; set; } }
    public interface MassProperty { double Mass { get; } }
    public interface Mate2 { int Type { get; } }
    public interface SelectionMgr { object CreateSelectData(); }
    /// <summary>Mark: standard mates (AddMate5) read their two entities from selection mark 1.</summary>
    public interface SelectData { int Mark { get; set; } }
}

namespace SolidWorks.Interop.swconst
{
    public enum swDocumentTypes_e { swDocNONE = 0, swDocPART = 1, swDocASSEMBLY = 2, swDocDRAWING = 3 }

    [System.Flags]
    public enum swOpenDocOptions_e { swOpenDocOptions_Silent = 1, swOpenDocOptions_ReadOnly = 2 }

    public enum swBodyType_e { swSolidBody = 0, swSheetBody = 1 }

    public enum swUserPreferenceStringValue_e { swDefaultTemplateAssembly = 9 }
    public enum swUserPreferenceIntegerValue_e { swUnitsLinear = 51 }
    public enum swUserPreferenceOption_e { swDetailingNoOptionSpecified = 0 }

    public enum swLengthUnit_e { swMM = 0, swCM = 1, swMETER = 2, swINCHES = 3, swFEET = 4, swFEETINCHES = 5, swANGSTROM = 6, swNANOMETER = 7, swMICRON = 8, swMIL = 9, swUIN = 10 }

    public enum swAddComponentConfigOptions_e { swAddComponentConfigOptions_CurrentSelectedConfig = 0 }

    public enum swMateType_e { swMateCOINCIDENT = 0, swMateCONCENTRIC = 1, swMatePERPENDICULAR = 2, swMatePARALLEL = 3, swMateTANGENT = 4, swMateDISTANCE = 5 }
    public enum swMateAlign_e { swMateAlignALIGNED = 0, swMateAlignANTI_ALIGNED = 1, swMateAlignCLOSEST = 2 }
    public enum swMateWidthOptions_e { swMateWidth_Centered = 0 }
    public enum swAddMateError_e { swAddMateError_ErrorUknown = 0, swAddMateError_NoError = 1, swAddMateError_IncorrectMateType = 2, swAddMateError_IncorrectAlignment = 3, swAddMateError_IncorrectSelections = 4, swAddMateError_OverDefinedAssembly = 5 }

    public enum swSaveAsVersion_e { swSaveAsCurrentVersion = 0 }

    [System.Flags]
    public enum swSaveAsOptions_e { swSaveAsOptions_Silent = 1, swSaveAsOptions_Copy = 2 }

    public enum swPersistReferencedObjectStates_e { swPersistReferencedObject_Ok = 0, swPersistReferencedObject_Deleted = 1, swPersistReferencedObject_Suppressed = 2, swPersistReferencedObject_Invalid = 3 }

    public enum swSelectType_e { swSelFACES = 2 }
}
