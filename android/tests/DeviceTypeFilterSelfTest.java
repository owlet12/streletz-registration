// Author: owlet (https://github.com/owlet12)
// Copyright (c) 2026 owlet
package ru.akchurin.streletzinit;

import java.util.*;
import org.json.*;

public final class DeviceTypeFilterSelfTest {
    static int checks;
    static void check(boolean ok, String message) { checks++; if (!ok) throw new AssertionError(message); }
    public static void main(String[] args) throws Exception {
        DeviceFilterIndex index = new DeviceFilterIndex();
        index.put(new DeviceFilterIndex.Row("a", "g1", "p1", 0, "3R.BTM207", "ИПР-ПРО"));
        index.put(new DeviceFilterIndex.Row("b", "g1", "p2", 2, "3R.BTM208", " ипр-про "));
        index.put(new DeviceFilterIndex.Row("c", "g2", "p1", 3, "3R.BIAL256", "Табло-ПРО"));
        index.put(new DeviceFilterIndex.Row("d", "g1", "p1", 1, "unknown", null));
        check(index.query("", 0, null, "", "ИПР-ПРО", () -> false).equals(Arrays.asList("a","b")), "type ignores case and edge whitespace");
        check(index.query("BTM207", 1, "g1", "p1", "ИПР-ПРО", () -> false).equals(Arrays.asList("a")), "all five filters intersect");
        check(index.query("", 2, "g1", "p1", "ИПР-ПРО", () -> false).isEmpty(), "panel and initialized stage cannot leak rows");
        check(index.query("", 0, null, "", "", () -> false).equals(Arrays.asList("d")), "missing type is selectable separately from all");
        check(index.query("", 0, null, "", null, () -> false).size() == 4, "all types includes missing type");
        check(index.query("", 0, null, "", "missing", () -> false).isEmpty(), "unknown type returns empty");
        check(index.query("", 0, null, "", "ИПР-ПРО", () -> true) == null, "stale filtered request cancelled");
        index.put(new DeviceFilterIndex.Row("a", "g1", "p1", 2, "3R.BTM207 updated", "Табло-ПРО"));
        check(index.query("", 0, null, "", "ИПР-ПРО", () -> false).equals(Arrays.asList("b")), "updated row removed from old type bucket");
        check(index.query("updated", 2, "g1", "p1", "Табло-ПРО", () -> false).equals(Arrays.asList("a")), "update changes type stage and search together");
        ClientScreenState state = new ClientScreenState(); state.fingerprint="project";state.deviceType="ипр-про";
        check(ClientScreenState.decode(state.encode(), "project").deviceType.equals("ипр-про"), "selected type survives screen restoration");
        state.deviceType="";
        check(ClientScreenState.decode(state.encode(), "project").deviceType.equals(""), "missing type survives restoration");
        state.deviceType=null;
        check(ClientScreenState.decode(state.encode(), "project").deviceType==null, "all types survives restoration");
        check(ClientScreenState.decode("{\"version\":1,\"fingerprint\":\"project\"}", "project").deviceType==null, "old saved screen defaults to all types");
        check(ClientScreenState.decode(state.encode(), "other")==null, "another project cannot reuse view state");
        List<ExportSnapshot.Row> rows=Arrays.asList(new ExportSnapshot.Row("r","Name","Description","1","g","ИПР-ПРО",1,false,null));
        ExportSnapshot snapshot=new ExportSnapshot(rows,"Synthetic","project",new JSONArray(),false,new ProjectPanelIndex(Collections.emptyList(),Collections.emptyMap()),
                Collections.emptyMap(),Collections.emptyMap(),Collections.emptySet());
        check(snapshot.prepareList().index.query("Description",0,null,"","ИПР-ПРО",()->false).equals(Arrays.asList("r")), "background initial index includes type");
        System.out.println("DeviceTypeFilterSelfTest: "+checks+" checks passed");
    }
}
