---
name: revit-families-and-3d
description: Create Revit families and 3D geometry through the FirstOption Revit MCP. Covers DirectShape for fast 3D, family documents (extrusions, parameters, types), and loading and placing family instances. Use when the user asks to model an object, build a .rfa family, or place custom 3D elements in Revit.
---

# Families and 3D in Revit

## Choose

| Need | Use |
|---|---|
| Show a shape fast; no types, no parameters | DirectShape in the project |
| A reusable, parametric, schedulable object | A loadable family (.rfa) |
| One special object in one model | Avoid in-place families; use a family |

Work in small steps: build, save, load, place. Check each step before the next.

## DirectShape (C#, one call, default transaction)

```csharp
double ft(double mm) => mm / 304.8;
var w = ft(1000); var d = ft(500); var h = ft(800);
var p0 = new XYZ(0, 0, 0); var p1 = new XYZ(w, 0, 0); var p2 = new XYZ(w, d, 0); var p3 = new XYZ(0, d, 0);
var loop = CurveLoop.Create(new List<Curve> { Line.CreateBound(p0, p1), Line.CreateBound(p1, p2), Line.CreateBound(p2, p3), Line.CreateBound(p3, p0) });
var solid = GeometryCreationUtilities.CreateExtrusionGeometry(new List<CurveLoop> { loop }, XYZ.BasisZ, h);
var ds = DirectShape.CreateElement(doc, new ElementId(BuiltInCategory.OST_GenericModel));
ds.SetShape(new GeometryObject[] { solid });
ds.Name = "FO Box";
return ds.Id;
```

## Family, step 1: build and save (Python, `use_transaction=false`)

```python
import os
tpl_dir = app.FamilyTemplatePath
tpl = os.path.join(tpl_dir, "Metric Generic Model.rft")
if not os.path.exists(tpl):
    print("templates: {}".format(os.listdir(tpl_dir)))
    raise Exception("template not found")

fam = app.NewFamilyDocument(tpl)
t = DB.Transaction(fam, "Build family")
t.Start()
mm = 1 / 304.8
w, d, h = 1000 * mm, 500 * mm, 800 * mm
pts = [DB.XYZ(0, 0, 0), DB.XYZ(w, 0, 0), DB.XYZ(w, d, 0), DB.XYZ(0, d, 0)]
profile = DB.CurveArray()
for i in range(4):
    profile.Append(DB.Line.CreateBound(pts[i], pts[(i + 1) % 4]))
arr = DB.CurveArrArray()
arr.Append(profile)
plane = DB.SketchPlane.Create(fam, DB.Plane.CreateByNormalAndOrigin(DB.XYZ.BasisZ, DB.XYZ.Zero))
fam.FamilyCreate.NewExtrusion(True, arr, plane, h)
t.Commit()

path = os.path.join(os.environ["TEMP"], "FO_Box.rfa")
opts = DB.SaveAsOptions()
opts.OverwriteExistingFile = True
fam.SaveAs(path, opts)
fam.Close(False)
result = path
```

Parameters: `fam.FamilyManager.AddParameter("Width", DB.GroupTypeId.Geometry, DB.SpecTypeId.Length, False)` in Revit 2022+. Revit 2021 uses `BuiltInParameterGroup` and `ParameterType`. Types: `FamilyManager.NewType("600x400")`, then `FamilyManager.Set(param, value)`.

## Family, step 2: load and place (Python, default transaction)

```python
import clr
path = args.get("path")
name = args.get("family", "FO_Box")
ref = clr.Reference[DB.Family]()
if doc.LoadFamily(path, ref):
    family = ref.Value
else:  # already in the model
    family = [f for f in DB.FilteredElementCollector(doc).OfClass(DB.Family) if f.Name == name][0]
symbol = doc.GetElement(list(family.GetFamilySymbolIds())[0])
if not symbol.IsActive:
    symbol.Activate()
    doc.Regenerate()
level = sorted(DB.FilteredElementCollector(doc).OfClass(DB.Level), key=lambda l: l.Elevation)[0]
inst = doc.Create.NewFamilyInstance(DB.XYZ(0, 0, 0), symbol, level, DB.Structure.StructuralType.NonStructural)
result = inst
```

## Edit a family that is in the model

Use C#. `doc.EditFamily(family)` must run outside a transaction (`use_transaction=false`). Change the family document in its own transaction, then call `familyDoc.LoadFamily(doc, new LoadOptions())` with a local class that implements `IFamilyLoadOptions` and returns `true` for overwrite.

## Check the result

- Print the bounding box: `el.get_BoundingBox(None)` (Python) or `el.get_BoundingBox(null)` (C#).
- Tell the user which view shows the element (3D view, section box).
- Save working steps to the library (`library_save`), one command per step.
